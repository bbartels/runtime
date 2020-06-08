// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Diagnostics;
using System.Buffers;
using System.Threading.Tasks;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace System.IO
{
    // This abstract base class represents a writer that can write
    // primitives to an arbitrary stream. A subclass can override methods to
    // give unique encodings.
    //
    public class BinaryWriter : IDisposable, IAsyncDisposable
    {
        public static readonly BinaryWriter Null = new BinaryWriter();

        protected Stream OutStream;
        private readonly byte[] _buffer;    // temp space for writing primitives to.
        private readonly Encoding _encoding;
        private readonly Encoder _encoder;

        private readonly bool _leaveOpen;

        // Size should be around the max number of chars/string * Encoding's max bytes/char

        // Protected default constructor that sets the output stream
        // to a null stream (a bit bucket).
        protected BinaryWriter()
        {
            OutStream = Stream.Null;
            _buffer = new byte[16];
            _encoding = EncodingCache.UTF8NoBOM;
            _encoder = _encoding.GetEncoder();
        }

        public BinaryWriter(Stream output) : this(output, EncodingCache.UTF8NoBOM, false)
        {
        }

        public BinaryWriter(Stream output, Encoding encoding) : this(output, encoding, false)
        {
        }

        public BinaryWriter(Stream output, Encoding encoding, bool leaveOpen)
        {
            if (output == null)
                throw new ArgumentNullException(nameof(output));
            if (encoding == null)
                throw new ArgumentNullException(nameof(encoding));
            if (!output.CanWrite)
                throw new ArgumentException(SR.Argument_StreamNotWritable);

            OutStream = output;
            _buffer = new byte[16];
            _encoding = encoding;
            _encoder = _encoding.GetEncoder();
            _leaveOpen = leaveOpen;
        }

        // Closes this writer and releases any system resources associated with the
        // writer. Following a call to Close, any operations on the writer
        // may raise exceptions.
        public virtual void Close()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_leaveOpen)
                    OutStream.Flush();
                else
                    OutStream.Close();
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public virtual ValueTask DisposeAsync()
        {
            try
            {
                if (GetType() == typeof(BinaryWriter))
                {
                    if (_leaveOpen)
                    {
                        return new ValueTask(OutStream.FlushAsync());
                    }

                    OutStream.Close();
                }
                else
                {
                    // Since this is a derived BinaryWriter, delegate to whatever logic
                    // the derived implementation already has in Dispose.
                    Dispose();
                }

                return default;
            }
            catch (Exception exc)
            {
                return ValueTask.FromException(exc);
            }
        }

        // Returns the stream associated with the writer. It flushes all pending
        // writes before returning. All subclasses should override Flush to
        // ensure that all buffered data is sent to the stream.
        public virtual Stream BaseStream
        {
            get
            {
                Flush();
                return OutStream;
            }
        }

        // Clears all buffers for this writer and causes any buffered data to be
        // written to the underlying device.
        public virtual void Flush() => OutStream.Flush();

        public virtual long Seek(int offset, SeekOrigin origin) => OutStream.Seek(offset, origin);

        // Writes a boolean to this stream. A single byte is written to the stream
        // with the value 0 representing false or the value 1 representing true.
        //
        public virtual void Write(bool value) => OutStream.WriteByte((byte)(value ? 1 : 0));

        // Writes a byte to this stream. The current position of the stream is
        // advanced by one.
        //
        public virtual void Write(byte value) => OutStream.WriteByte(value);

        // Writes a signed byte to this stream. The current position of the stream
        // is advanced by one.
        //
        [CLSCompliant(false)]
        public virtual void Write(sbyte value) => OutStream.WriteByte((byte)value);

        // Writes a byte array to this stream.
        //
        // This default implementation calls the Write(Object, int, int)
        // method to write the byte array.
        //
        public virtual void Write(byte[] buffer)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            OutStream.Write(buffer, 0, buffer.Length);
        }

        // Writes a section of a byte array to this stream.
        //
        // This default implementation calls the Write(Object, int, int)
        // method to write the byte array.
        //
        public virtual void Write(byte[] buffer, int index, int count) => OutStream.Write(buffer, index, count);


        // Writes a character to this stream. The current position of the stream is
        // advanced by two.
        // Note this method cannot handle surrogates properly in UTF-8.
        //
        public virtual void Write(char ch)
        {
            if (char.IsSurrogate(ch))
                throw new ArgumentException(SR.Arg_SurrogatesNotAllowedAsSingleChar);

            Debug.Assert(_encoding.GetMaxByteCount(1) <= 16, "_encoding.GetMaxByteCount(1) <= 16)");

            int numBytes = _encoder.GetBytes(MemoryMarshal.CreateReadOnlySpan(ref ch, 1), _buffer, flush: true);
            OutStream.Write(_buffer, 0, numBytes);
        }

        // Writes a character array to this stream.
        //
        // This default implementation calls the Write(Object, int, int)
        // method to write the character array.
        //
        public virtual void Write(char[] chars) => WriteChars(chars, false);

        // Writes a section of a character array to this stream.
        //
        // This default implementation calls the Write(Object, int, int)
        // method to write the character array.
        //
        public virtual void Write(char[] chars, int index, int count) => WriteChars(chars.AsSpan(index, count), false);

        // Writes a double to this stream. The current position of the stream is
        // advanced by eight.
        //
        public virtual unsafe void Write(double value)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer, value);
            OutStream.Write(_buffer, 0, 8);
        }

        public virtual void Write(decimal value)
        {
            decimal.GetBytes(value, _buffer);
            OutStream.Write(_buffer, 0, 16);
        }

        // Writes a two-byte signed integer to this stream. The current position of
        // the stream is advanced by two.
        //
        public virtual void Write(short value)
        {
            _buffer[0] = (byte)value;
            _buffer[1] = (byte)(value >> 8);
            OutStream.Write(_buffer, 0, 2);
        }

        // Writes a two-byte unsigned integer to this stream. The current position
        // of the stream is advanced by two.
        //
        [CLSCompliant(false)]
        public virtual void Write(ushort value)
        {
            _buffer[0] = (byte)value;
            _buffer[1] = (byte)(value >> 8);
            OutStream.Write(_buffer, 0, 2);
        }

        // Writes a four-byte signed integer to this stream. The current position
        // of the stream is advanced by four.
        //
        public virtual void Write(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer, value);
            OutStream.Write(_buffer, 0, 4);
        }

        // Writes a four-byte unsigned integer to this stream. The current position
        // of the stream is advanced by four.
        //
        [CLSCompliant(false)]
        public virtual void Write(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer, value);
            OutStream.Write(_buffer, 0, 4);
        }

        // Writes an eight-byte signed integer to this stream. The current position
        // of the stream is advanced by eight.
        //
        public virtual void Write(long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer, value);
            OutStream.Write(_buffer, 0, 8);
        }

        // Writes an eight-byte unsigned integer to this stream. The current
        // position of the stream is advanced by eight.
        //
        [CLSCompliant(false)]
        public virtual void Write(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer, value);
            OutStream.Write(_buffer, 0, 8);
        }

        // Writes a float to this stream. The current position of the stream is
        // advanced by four.
        //
        public virtual unsafe void Write(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_buffer, value);
            OutStream.Write(_buffer, 0, 4);
        }

        // Writes a length-prefixed string to this stream in the BinaryWriter's
        // current Encoding. This method first writes the length of the string as
        // a four-byte unsigned integer, and then writes that many characters
        // to the stream.
        //
        public virtual void Write(string value) => WriteChars(value, true);

        [SkipLocalsInit]
        private void WriteChars(ReadOnlySpan<char> value, bool isString)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));

            int len = _encoding.GetByteCount(value);
            if (isString)
            {
                Write7BitEncodedInt(len);
            }

            Span<byte> encodedChars = stackalloc byte[512];

            if (len <= 512)
            {
                _encoding.GetBytes(value, encodedChars);
                OutStream.Write(encodedChars.Slice(0, len));
                return;
            }

            int charStart = 0;

            while (len > 0)
            {
                _encoder.Convert(value.Slice(charStart), encodedChars, false, out int charCount, out int byteCount, out bool completed);
                if (completed)
                {
                    _encoder.Reset();
                }

                OutStream.Write(encodedChars.Slice(0, byteCount));
                charStart += charCount;
                len -= byteCount;
            }
        }

        public virtual void Write(ReadOnlySpan<byte> buffer)
        {
            if (GetType() == typeof(BinaryWriter))
            {
                OutStream.Write(buffer);
            }
            else
            {
                byte[] array = ArrayPool<byte>.Shared.Rent(buffer.Length);
                try
                {
                    buffer.CopyTo(array);
                    Write(array, 0, buffer.Length);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(array);
                }
            }
        }

        public virtual void Write(ReadOnlySpan<char> chars)
        {
            byte[] bytes = ArrayPool<byte>.Shared.Rent(_encoding.GetMaxByteCount(chars.Length));
            try
            {
                int bytesWritten = _encoding.GetBytes(chars, bytes);
                Write(bytes, 0, bytesWritten);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        public void Write7BitEncodedInt(int value)
        {
            uint uValue = (uint)value;

            // Write out an int 7 bits at a time. The high bit of the byte,
            // when on, tells reader to continue reading more bytes.
            //
            // Using the constants 0x7F and ~0x7F below offers smaller
            // codegen than using the constant 0x80.

            while (uValue > 0x7Fu)
            {
                Write((byte)(uValue | ~0x7Fu));
                uValue >>= 7;
            }

            Write((byte)uValue);
        }

        public void Write7BitEncodedInt64(long value)
        {
            ulong uValue = (ulong)value;

            // Write out an int 7 bits at a time. The high bit of the byte,
            // when on, tells reader to continue reading more bytes.
            //
            // Using the constants 0x7F and ~0x7F below offers smaller
            // codegen than using the constant 0x80.

            while (uValue > 0x7Fu)
            {
                Write((byte)((uint)uValue | ~0x7Fu));
                uValue >>= 7;
            }

            Write((byte)uValue);
        }
    }
}
