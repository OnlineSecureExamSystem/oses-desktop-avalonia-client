// This source file's Lempel-Ziv-Welch algorithm is derived from Chromium's Android GifPlayer
// as seen here (https://github.com/chromium/chromium/blob/master/third_party/gif_player/src/jp/tomorrowkey/android/gifplayer)
// Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
// Copyright (C) 2015 The Gifplayer Authors. All Rights Reserved.

// The rest of the source file is licensed under MIT License.
// Copyright (C) 2018 Jumar A. Macato, All Rights Reserved.

using AvaloniaGif.Caching;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static AvaloniaGif.Extensions.StreamExtensions;

namespace AvaloniaGif.Decoding
{
    public sealed class GifDecoder : IDisposable
    {
        private static readonly ReadOnlyMemory<byte> G87AMagic
            = Encoding.ASCII.GetBytes("GIF87a").AsMemory();

        private static readonly ReadOnlyMemory<byte> G89AMagic
            = Encoding.ASCII.GetBytes("GIF89a").AsMemory();

        private static readonly ReadOnlyMemory<byte> NetscapeMagic
            = Encoding.ASCII.GetBytes("NETSCAPE2.0").AsMemory();

        private static readonly TimeSpan FrameDelayThreshold = TimeSpan.FromMilliseconds(10);
        private static readonly TimeSpan FrameDelayDefault = TimeSpan.FromMilliseconds(100);
        private static readonly GifColor TransparentColor = new GifColor(0, 0, 0, 0);
        private static readonly XXHash64 Hasher = new XXHash64();
        private static readonly int MaxTempBuf = 768;
        private static readonly int MaxStackSize = 4096;
        private static readonly int MaxBits = 4097;

        private readonly Stream _fileStream;
        private readonly object _lockObj;
        private readonly bool _hasFrameBackups;

        private int _gctSize, _bgIndex, _prevFrame;
        private bool _gctUsed;

        private GifHeader _gifHeader;
        private GifRect _gifDimensions;
        private ulong _globalColorTable;
        private readonly int _backBufferBytes;
        private GifColor[] _bitmapBackBuffer;

        private short[] _prefixBuf;
        private byte[] _suffixBuf;
        private byte[] _pixelStack;
        private byte[] _indexBuf;
        private byte[] _prevFrameIndexBuf;
        internal volatile bool _hasNewFrame;

        public GifHeader Header => _gifHeader;
        public List<GifFrame> Frames = new List<GifFrame>();

        internal static ICache<ulong, GifColor[]> GlobalColorTableCache
            = Caches.KeyValue<ulong, GifColor[]>()
                .WithBackgroundPurge(TimeSpan.FromSeconds(30))
                .WithExpiration(TimeSpan.FromSeconds(10))
                .WithSlidingExpiration()
                .Build();

        public GifDecoder(Stream fileStream)
        {
            _fileStream = fileStream;
            _lockObj = new object();

            ProcessHeaderData();
            ProcessFrameData();

            if (Header.Iterations == -1)
                Header.IterationCount = new GifRepeatBehavior() { Count = 1 };
            else if (Header.Iterations == 0)
                Header.IterationCount = new GifRepeatBehavior() { LoopForever = true };
            else if (Header.Iterations > 0)
                Header.IterationCount = new GifRepeatBehavior() { Count = Header.Iterations };

            var pixelCount = _gifDimensions.TotalPixels;

            _hasFrameBackups = Frames
                .Any(f => f.FrameDisposalMethod == FrameDisposal.Restore);

            _bitmapBackBuffer = new GifColor[pixelCount];
            _indexBuf = new byte[pixelCount];

            if (_hasFrameBackups)
                _prevFrameIndexBuf = new byte[pixelCount];

            _prefixBuf = new short[MaxStackSize];
            _suffixBuf = new byte[MaxStackSize];
            _pixelStack = new byte[MaxStackSize + 1];

            _backBufferBytes = (pixelCount * Marshal.SizeOf(typeof(GifColor)));
        }

        public void Dispose()
        {
            lock (_lockObj)
            {
                Frames.Clear();

                _bitmapBackBuffer = null;
                _prefixBuf = null;
                _suffixBuf = null;
                _pixelStack = null;
                _indexBuf = null;
                _prevFrameIndexBuf = null;

                _fileStream?.Dispose();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int PixCoord(int x, int y) => x + (y * _gifDimensions.Width);

        static readonly (int Start, int Step)[] Pass =
        {
            (0, 8),
            (4, 8),
            (2, 4),
            (1, 2)
        };

        private static readonly Action<int, Action<int>> InterlaceRows = (height, RowAction) =>
        {
            for (int i = 0; i < 4; i++)
            {
                var curPass = Pass[i];
                var y = curPass.Start;
                while (y < height)
                {
                    RowAction(y);
                    y += curPass.Step;
                }
            }
        };

        private static readonly Action<int, Action<int>> NormalRows = (height, RowAction) =>
        {
            for (int i = 0; i < height; i++)
                RowAction(i);
        };


        internal void ClearImage()
        {
            ClearArea(_gifDimensions);
        }

        public void RenderFrame(int fIndex)
        {
            lock (_lockObj)
            {
                if (fIndex < 0 | fIndex >= Frames.Count)
                    return;

                if (fIndex == 0)
                    ClearImage();

                var tmpB = ArrayPool<byte>.Shared.Rent(MaxTempBuf);

                var curFrame = Frames[fIndex];

                DisposePreviousFrame();

                DecompressFrameToIndexBuffer(curFrame, _indexBuf, tmpB);

                if (_hasFrameBackups & curFrame.ShouldBackup)
                    Buffer.BlockCopy(_indexBuf, 0, _prevFrameIndexBuf, 0, curFrame.Dimensions.TotalPixels);

                DrawFrame(curFrame, _indexBuf);

                _prevFrame = fIndex;
                _hasNewFrame = true;

                ArrayPool<byte>.Shared.Return(tmpB);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DrawFrame(GifFrame curFrame, Memory<byte> _frameIndexSpan)
        {
            var activeColorTableHash = curFrame.IsLocalColorTableUsed ? curFrame.LocalColorTableCacheID : _globalColorTable;
            var activeColorTable = GlobalColorTableCache.Get(activeColorTableHash);

            var cX = curFrame.Dimensions.X;
            var cY = curFrame.Dimensions.Y;
            var cH = curFrame.Dimensions.Height;
            var cW = curFrame.Dimensions.Width;
            var tC = curFrame.TransparentColorIndex;
            var hT = curFrame.HasTransparency;

            if (curFrame.IsInterlaced)
                InterlaceRows(cH, DrawRow);
            else
                NormalRows(cH, DrawRow);

            //for (var row = 0; row < cH; row++)
            void DrawRow(int row)
            {
                // Get the starting point of the current row on frame's index stream.
                var indexOffset = row * cW;

                // Get the target backbuffer offset from the frames coords.
                var targetOffset = PixCoord(cX, row + cY);
                var len = _bitmapBackBuffer.Length;

                for (var i = 0; i < cW; i++)
                {
                    var indexColor = _frameIndexSpan.Span[indexOffset + i];

                    if (activeColorTable == null || targetOffset >= len || indexColor >= activeColorTable.Length) return;

                    if (!(hT & indexColor == tC))
                        _bitmapBackBuffer[targetOffset] = activeColorTable[indexColor];

                    targetOffset++;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DisposePreviousFrame()
        {
            var prevFrame = Frames[_prevFrame];

            switch (prevFrame.FrameDisposalMethod)
            {
                case FrameDisposal.Background:
                    ClearArea(prevFrame.Dimensions);
                    break;
                case FrameDisposal.Restore:
                    if (_hasFrameBackups)
                        DrawFrame(prevFrame, _prevFrameIndexBuf);
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ClearArea(GifRect area)
        {
            for (int y = 0; y < area.Height; y++)
            {
                var targetOffset = PixCoord(area.X, y + area.Y);
                for (int x = 0; x < area.Width; x++)
                    _bitmapBackBuffer[targetOffset + x] = TransparentColor;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void DecompressFrameToIndexBuffer(GifFrame curFrame, Span<byte> indexSpan, byte[] tempBuf)
        {
            var str = _fileStream;

            str.Position = curFrame.LZWStreamPosition;
            var totalPixels = curFrame.Dimensions.TotalPixels;

            // Initialize GIF data stream decoder.
            var dataSize = curFrame.LZWMinCodeSize;
            var clear = 1 << dataSize;
            var endOfInformation = clear + 1;
            var available = clear + 2;
            var oldCode = -1;
            var codeSize = dataSize + 1;
            var codeMask = (1 << codeSize) - 1;

            for (var code = 0; code < clear; code++)
            {
                _prefixBuf[code] = 0;
                _suffixBuf[code] = (byte)code;
            }

            // Decode GIF pixel stream.
            int bits, first, top, pixelIndex, blockPos;
            var datum = bits = first = top = pixelIndex = blockPos = 0;

            while (pixelIndex < totalPixels)
            {
                var blockSize = str.ReadBlock(tempBuf);

                if (blockSize == 0)
                    break;

                blockPos = 0;

                while (blockPos < blockSize)
                {
                    datum += tempBuf[blockPos] << bits;
                    blockPos++;

                    bits += 8;

                    while (bits >= codeSize)
                    {
                        // Get the next code.
                        var code = datum & codeMask;
                        datum >>= codeSize;
                        bits -= codeSize;

                        // Interpret the code
                        if (code == clear)
                        {
                            // Reset decoder.
                            codeSize = dataSize + 1;
                            codeMask = (1 << codeSize) - 1;
                            available = clear + 2;
                            oldCode = -1;
                            continue;
                        }

                        // Check for explicit end-of-stream
                        if (code == endOfInformation)
                            return;

                        if (oldCode == -1)
                        {
                            indexSpan[pixelIndex++] = _suffixBuf[code];
                            oldCode = code;
                            first = code;
                            continue;
                        }

                        var inCode = code;
                        if (code >= available)
                        {
                            _pixelStack[top++] = (byte)first;
                            code = oldCode;

                            if (top == MaxBits)
                                ThrowException();
                        }

                        while (code >= clear)
                        {
                            if (code >= MaxBits || code == _prefixBuf[code])
                                ThrowException();

                            _pixelStack[top++] = _suffixBuf[code];
                            code = _prefixBuf[code];

                            if (top == MaxBits)
                                ThrowException();
                        }

                        first = _suffixBuf[code];
                        _pixelStack[top++] = (byte)first;

                        // Add new code to the dictionary
                        if (available < MaxStackSize)
                        {
                            _prefixBuf[available] = (short)oldCode;
                            _suffixBuf[available] = (byte)first;
                            available++;

                            if (((available & codeMask) == 0) && (available < MaxStackSize))
                            {
                                codeSize++;
                                codeMask += available;
                            }
                        }

                        oldCode = inCode;

                        // Drain the pixel stack.
                        do
                        {
                            indexSpan[pixelIndex++] = _pixelStack[--top];
                        } while (top > 0);
                    }
                }
            }

            while (pixelIndex < totalPixels)
                indexSpan[pixelIndex++] = 0; // clear missing pixels

            void ThrowException() => throw new LzwDecompressionException();
        }

        /// <summary>
        /// Directly copies the <see cref="GifColor"/> struct array to a bitmap IntPtr.
        /// </summary>
        public void WriteBackBufToFb(IntPtr targetPointer)
        {
            if (_hasNewFrame & _bitmapBackBuffer != null)
                lock (_lockObj)
                    unsafe
                    {
                        fixed (void* src = &_bitmapBackBuffer[0])
                            Buffer.MemoryCopy(src, targetPointer.ToPointer(), (uint)_backBufferBytes, (uint)_backBufferBytes);
                        _hasNewFrame = false;
                    }
        }

        /// <summary>
        /// Processes GIF Header.
        /// </summary>
        private void ProcessHeaderData()
        {
            var str = _fileStream;
            var tmpB = ArrayPool<byte>.Shared.Rent(MaxTempBuf);
            var tempBuf = tmpB.AsSpan();


            str.Read(tmpB, 0, 6);

            if (!tempBuf.Slice(0, 3).SequenceEqual(G87AMagic.Slice(0, 3).Span))
                throw new InvalidGifStreamException("Not a GIF stream.");

            if (!(tempBuf.Slice(0, 6).SequenceEqual(G87AMagic.Span) | tempBuf.Slice(0, 6).SequenceEqual(G89AMagic.Span)))
                throw new InvalidGifStreamException("Unsupported GIF Version: " +
                                                    Encoding.ASCII.GetString(tempBuf.Slice(0, 6).ToArray()));

            ProcessScreenDescriptor(tmpB);

            if (_gctUsed)
                _globalColorTable = ProcessColorTable(ref str, tmpB, _gctSize);


            _gifHeader = new GifHeader()
            {
                Dimensions = _gifDimensions,
                HasGlobalColorTable = _gctUsed,
                GlobalColorTableCacheID = _globalColorTable,
                GlobalColorTableSize = _gctSize,
                BackgroundColorIndex = _bgIndex,
                HeaderSize = _fileStream.Position
            };

            ArrayPool<byte>.Shared.Return(tmpB);
        }

        static MemoryStream memoryStream = new MemoryStream();

        /// <summary>
        /// Parses colors from file stream to target color table.
        /// </summary> 
        private static ulong ProcessColorTable(ref Stream stream, byte[] rawBufSpan, int nColors)
        {
            var nBytes = 3 * nColors;
            var targ = new GifColor[nColors];

            var n = stream.Read(rawBufSpan, 0, nBytes);

            if (n < nBytes)
                throw new InvalidOperationException("Wrong color table bytes.");

            Hasher.ComputeHash(rawBufSpan, 0, nBytes);

            var tableHash = Hasher.HashUInt64;

            int i = 0, j = 0;

            while (i < nColors)
            {
                var r = rawBufSpan[j++];
                var g = rawBufSpan[j++];
                var b = rawBufSpan[j++];
                targ[i++] = new GifColor(r, g, b);
            }

            GlobalColorTableCache.Set(tableHash, targ);

            return tableHash;
        }

        /// <summary>
        /// Parses screen and other GIF descriptors. 
        /// </summary>
        private void ProcessScreenDescriptor(byte[] tempBuf)
        {
            var str = _fileStream;

            var _width = str.ReadUShortS(tempBuf);
            var _height = str.ReadUShortS(tempBuf);

            var packed = str.ReadByteS(tempBuf);

            _gctUsed = (packed & 0x80) != 0;
            _gctSize = 2 << (packed & 7);
            _bgIndex = str.ReadByteS(tempBuf);

            _gifDimensions = new GifRect(0, 0, _width, _height);
            str.Skip(1);
        }

        /// <summary>
        /// Parses all frame data.
        /// </summary>
        private void ProcessFrameData()
        {
            var str = _fileStream;
            str.Position = _gifHeader.HeaderSize;

            var tempBuf = ArrayPool<byte>.Shared.Rent(MaxTempBuf);

            var terminate = false;
            var curFrame = 0;

            Frames.Add(new GifFrame());

            do
            {
                var blockType = (BlockTypes)str.ReadByteS(tempBuf);

                switch (blockType)
                {
                    case BlockTypes.EMPTY:
                        break;

                    case BlockTypes.EXTENSION:
                        ProcessExtensions(ref curFrame, tempBuf);
                        break;

                    case BlockTypes.IMAGE_DESCRIPTOR:
                        ProcessImageDescriptor(ref curFrame, tempBuf);
                        str.SkipBlocks(tempBuf);
                        break;

                    case BlockTypes.TRAILER:
                        Frames.RemoveAt(Frames.Count - 1);
                        terminate = true;
                        break;

                    default:
                        str.SkipBlocks(tempBuf);
                        break;
                }

                // Break the loop when the stream is not valid anymore.
                if (str.Position >= str.Length & terminate == false)
                    throw new InvalidProgramException("Reach the end of the filestream without trailer block.");
            } while (!terminate);

            ArrayPool<byte>.Shared.Return(tempBuf);
        }

        /// <summary>
        /// Parses GIF Image Descriptor Block.
        /// </summary>
        private void ProcessImageDescriptor(ref int curFrame, byte[] tempBuf)
        {
            var str = _fileStream;
            var currentFrame = Frames[curFrame];

            // Parse frame dimensions.
            var _frameX = str.ReadUShortS(tempBuf);
            var _frameY = str.ReadUShortS(tempBuf);
            var _frameW = str.ReadUShortS(tempBuf);
            var _frameH = str.ReadUShortS(tempBuf);

            _frameW = (ushort)Math.Min(_frameW, _gifDimensions.Width - _frameX);
            _frameH = (ushort)Math.Min(_frameH, _gifDimensions.Height - _frameY);

            currentFrame.Dimensions = new GifRect(_frameX, _frameY, _frameW, _frameH);

            // Unpack interlace and lct info.
            var packed = str.ReadByteS(tempBuf);
            currentFrame.IsInterlaced = (packed & 0x40) != 0;
            currentFrame.IsLocalColorTableUsed = (packed & 0x80) != 0;
            currentFrame.LocalColorTableSize = (int)Math.Pow(2, (packed & 0x07) + 1);

            if (currentFrame.IsLocalColorTableUsed)
                currentFrame.LocalColorTableCacheID = ProcessColorTable(ref str, tempBuf, currentFrame.LocalColorTableSize);

            currentFrame.LZWMinCodeSize = str.ReadByteS(tempBuf);
            currentFrame.LZWStreamPosition = str.Position;

            curFrame += 1;
            Frames.Add(new GifFrame());
        }

        /// <summary>
        /// Parses GIF Extension Blocks.
        /// </summary>
        private void ProcessExtensions(ref int curFrame, byte[] tempBuf)
        {
            var str = _fileStream;

            var extType = (ExtensionType)str.ReadByteS(tempBuf);

            switch (extType)
            {
                case ExtensionType.GRAPHICS_CONTROL:

                    str.ReadBlock(tempBuf);
                    var currentFrame = Frames[curFrame];
                    var packed = tempBuf[0];

                    currentFrame.FrameDisposalMethod = (FrameDisposal)((packed & 0x1c) >> 2);

                    if (currentFrame.FrameDisposalMethod != FrameDisposal.Restore)
                        currentFrame.ShouldBackup = true;

                    currentFrame.HasTransparency = (packed & 1) != 0;

                    currentFrame.FrameDelay =
                        TimeSpan.FromMilliseconds(SpanToShort(tempBuf.AsSpan(1)) * 10);

                    if (currentFrame.FrameDelay <= FrameDelayThreshold)
                        currentFrame.FrameDelay = FrameDelayDefault;

                    currentFrame.TransparentColorIndex = tempBuf[3];
                    break;

                case ExtensionType.APPLICATION:
                    var blockLen = str.ReadBlock(tempBuf);
                    var blockSpan = tempBuf.AsSpan(0, blockLen);
                    var blockHeader = tempBuf.AsSpan(0, NetscapeMagic.Length);

                    if (blockHeader.SequenceEqual(NetscapeMagic.Span))
                    {
                        var count = 1;

                        while (count > 0)
                            count = str.ReadBlock(tempBuf);

                        var _iterationCount = SpanToShort(tempBuf.AsSpan(1));

                        _gifHeader.Iterations = _iterationCount;
                    }
                    else
                        str.SkipBlocks(tempBuf);
                    break;

                default:
                    str.SkipBlocks(tempBuf);
                    break;
            }
        }
    }
}
