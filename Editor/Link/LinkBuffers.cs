using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbiters.Mixer.Editor
{
    /// <summary>
    /// Writes a link payload into one growing buffer: scalars little-endian, arrays as whole blocks (the bytes of a
    /// blittable span copied at once), sections and counts patched in place once known. Nothing goes through a stream,
    /// and nothing is copied again until <see cref="ToArray"/> trims the result.
    /// </summary>
    internal sealed class LinkWriter
    {
        static LinkWriter()
        {
            // Blocks are the arrays' own bytes: every editor platform Unity runs on is little-endian, as the wire is.
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("The Blender link needs a little-endian machine.");
        }

        private byte[] buffer;
        private int length;

        public LinkWriter(int capacity = 256) { buffer = new byte[Math.Max(16, capacity)]; }

        public int Length => length;

        private void Ensure(int more)
        {
            long need = (long)length + more;
            if (need <= buffer.Length) return;
            if (need > int.MaxValue) throw new InvalidDataException("A link payload of more than 2 GB.");
            long size = Math.Max(need, (long)buffer.Length * 2);
            Array.Resize(ref buffer, (int)Math.Min(int.MaxValue, size));
        }

        public void U8(byte value) { Ensure(1); buffer[length++] = value; }
        public void U16(ushort value) { Ensure(2); BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, length, 2), value); length += 2; }
        public void I32(int value) { Ensure(4); BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(buffer, length, 4), value); length += 4; }
        public void U64(ulong value) { Ensure(8); BinaryPrimitives.WriteUInt64LittleEndian(new Span<byte>(buffer, length, 8), value); length += 8; }
        public void F32(float value) => I32(BitConverter.SingleToInt32Bits(value));

        /// <summary>u16 byte length, then the UTF-8 bytes (cut at 65535 bytes).</summary>
        public void String(string value)
        {
            value = value ?? "";
            int count = Encoding.UTF8.GetByteCount(value);
            if (count > ushort.MaxValue)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                U16(ushort.MaxValue);
                Ensure(ushort.MaxValue);
                Buffer.BlockCopy(bytes, 0, buffer, length, ushort.MaxValue);
                length += ushort.MaxValue;
                return;
            }
            U16((ushort)count);
            Ensure(count);
            length += Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, length);
        }

        /// <summary>The bytes of the first <paramref name="count"/> elements of an array, at once.</summary>
        public void Block<T>(T[] values, int count) where T : unmanaged => Block(new ReadOnlySpan<T>(values, 0, count));

        public void Block<T>(ReadOnlySpan<T> values) where T : unmanaged
        {
            var bytes = MemoryMarshal.AsBytes(values);
            Ensure(bytes.Length);
            bytes.CopyTo(new Span<byte>(buffer, length, bytes.Length));
            length += bytes.Length;
        }

        /// <summary>
        /// Room for <paramref name="count"/> elements, as a span to fill in place. The span is valid until the next write
        /// (which may move the buffer): fill it first.
        /// </summary>
        public unsafe Span<T> Claim<T>(int count) where T : unmanaged
        {
            int bytes = checked(count * sizeof(T));
            Ensure(bytes);
            var span = new Span<byte>(buffer, length, bytes);
            length += bytes;
            return MemoryMarshal.Cast<byte, T>(span);
        }

        /// <summary>A u16 or i32 written later (a count known once its elements were written): its place.</summary>
        public int ReserveU16() { int at = length; U16(0); return at; }
        public int ReserveI32() { int at = length; I32(0); return at; }
        public void PatchU16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(new Span<byte>(buffer, at, 2), value);
        public void PatchI32(int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(new Span<byte>(buffer, at, 4), value);

        /// <summary>u8 tag, then an i32 byte length patched by <see cref="EndSection"/>.</summary>
        public int BeginSection(byte tag) { U8(tag); return ReserveI32(); }
        public void EndSection(int at) => PatchI32(at, length - (at + 4));

        public byte[] ToArray()
        {
            if (length == buffer.Length) return buffer;
            var result = new byte[length];
            Buffer.BlockCopy(buffer, 0, result, 0, length);
            return result;
        }
    }

    /// <summary>
    /// Reads a link payload: scalars little-endian, arrays as whole blocks. Every count is checked against the bytes left
    /// before anything is allocated, so a payload that does not hold together throws <see cref="InvalidDataException"/>
    /// instead of allocating what it announces.
    /// </summary>
    internal sealed class LinkReader
    {
        private readonly byte[] data;
        private readonly string what;
        private int at;

        public LinkReader(byte[] data, string what)
        {
            this.data = data ?? throw new InvalidDataException("No " + what + " payload.");
            this.what = what;
        }

        public int Position { get => at; set => at = value; }
        public int Length => data.Length;
        public int Left => data.Length - at;

        private void Need(long count)
        {
            if (count < 0 || count > Left) throw new InvalidDataException("The " + what + " payload ends too soon.");
        }

        /// <summary>A count read from the payload, when that many elements of at least <paramref name="bytesEach"/> bytes fit in what is left.</summary>
        public int Count(int count, int bytesEach, string of)
        {
            if (count < 0 || (long)count * bytesEach > Left) throw new InvalidDataException("The payload announces " + count + " " + of + " but holds " + Left + " bytes more.");
            return count;
        }

        public byte U8() { Need(1); return data[at++]; }
        public ushort U16() { Need(2); var v = BinaryPrimitives.ReadUInt16LittleEndian(new ReadOnlySpan<byte>(data, at, 2)); at += 2; return v; }
        public int I32() { Need(4); var v = BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(data, at, 4)); at += 4; return v; }
        public ulong U64() { Need(8); var v = BinaryPrimitives.ReadUInt64LittleEndian(new ReadOnlySpan<byte>(data, at, 8)); at += 8; return v; }
        public float F32() => BitConverter.Int32BitsToSingle(I32());

        public string String()
        {
            int length = U16();
            Need(length);
            string value = Encoding.UTF8.GetString(data, at, length);
            at += length;
            return value;
        }

        /// <summary><paramref name="count"/> elements read as one block into a new array.</summary>
        public unsafe T[] Array<T>(int count) where T : unmanaged
        {
            long bytes = (long)count * sizeof(T);
            Need(bytes);
            var result = new T[count];
            MemoryMarshal.Cast<byte, T>(new ReadOnlySpan<byte>(data, at, (int)bytes)).CopyTo(result);
            at += (int)bytes;
            return result;
        }
    }
}
