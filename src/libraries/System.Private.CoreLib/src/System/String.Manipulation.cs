// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using Internal.Runtime.CompilerServices;

namespace System
{
    public partial class String
    {
        private const int StackallocIntBufferSizeLimit = 128;

        private static void FillStringChecked(string dest, int destPos, string src)
        {
            Debug.Assert(dest != null);
            Debug.Assert(src != null);
            if (src.Length > dest.Length - destPos)
            {
                throw new IndexOutOfRangeException();
            }

            Buffer.Memmove(
                destination: ref Unsafe.Add(ref dest._firstChar, destPos),
                source: ref src._firstChar,
                elementCount: (uint)src.Length);
        }

        public static string Concat(object? arg0) => arg0?.ToString() ?? string.Empty;

        public static string Concat(object? arg0, object? arg1)
        {
            if (arg0 == null)
            {
                arg0 = string.Empty;
            }

            if (arg1 == null)
            {
                arg1 = string.Empty;
            }
            return Concat(arg0.ToString(), arg1.ToString());
        }

        public static string Concat(object? arg0, object? arg1, object? arg2)
        {
            if (arg0 == null)
            {
                arg0 = string.Empty;
            }

            if (arg1 == null)
            {
                arg1 = string.Empty;
            }

            if (arg2 == null)
            {
                arg2 = string.Empty;
            }

            return Concat(arg0.ToString(), arg1.ToString(), arg2.ToString());
        }

        public static string Concat(params object?[] args)
        {
            if (args == null)
            {
                throw new ArgumentNullException(nameof(args));
            }

            if (args.Length <= 1)
            {
                return args.Length == 0 ?
                    string.Empty :
                    args[0]?.ToString() ?? string.Empty;
            }

            // We need to get an intermediary string array
            // to fill with each of the args' ToString(),
            // and then just concat that in one operation.

            // This way we avoid any intermediary string representations,
            // or buffer resizing if we use StringBuilder (although the
            // latter case is partially alleviated due to StringBuilder's
            // linked-list style implementation)

            var strings = new string[args.Length];

            int totalLength = 0;

            for (int i = 0; i < args.Length; i++)
            {
                object? value = args[i];

                string toString = value?.ToString() ?? string.Empty; // We need to handle both the cases when value or value.ToString() is null
                strings[i] = toString;

                totalLength += toString.Length;

                if (totalLength < 0) // Check for a positive overflow
                {
                    throw new OutOfMemoryException();
                }
            }

            // If all of the ToStrings are null/empty, just return string.Empty
            if (totalLength == 0)
            {
                return string.Empty;
            }

            string result = FastAllocateString(totalLength);
            int position = 0; // How many characters we've copied so far

            for (int i = 0; i < strings.Length; i++)
            {
                string s = strings[i];

                Debug.Assert(s != null);
                Debug.Assert(position <= totalLength - s.Length, "We didn't allocate enough space for the result string!");

                FillStringChecked(result, position, s);
                position += s.Length;
            }

            return result;
        }

        public static string Concat<T>(IEnumerable<T> values)
        {
            if (values == null)
                throw new ArgumentNullException(nameof(values));

            if (typeof(T) == typeof(char))
            {
                // Special-case T==char, as we can handle that case much more efficiently,
                // and string.Concat(IEnumerable<char>) can be used as an efficient
                // enumerable-based equivalent of new string(char[]).
                using (IEnumerator<char> en = Unsafe.As<IEnumerable<char>>(values).GetEnumerator())
                {
                    if (!en.MoveNext())
                    {
                        // There weren't any chars.  Return the empty string.
                        return Empty;
                    }

                    char c = en.Current; // save the first char

                    if (!en.MoveNext())
                    {
                        // There was only one char.  Return a string from it directly.
                        return CreateFromChar(c);
                    }

                    // Create the StringBuilder, add the chars we've already enumerated,
                    // add the rest, and then get the resulting string.
                    var result = new ValueStringBuilder(stackalloc char[256]);
                    result.Append(c); // first value
                    do
                    {
                        c = en.Current;
                        result.Append(c);
                    }
                    while (en.MoveNext());
                    return result.ToString();
                }
            }
            else
            {
                using (IEnumerator<T> en = values.GetEnumerator())
                {
                    if (!en.MoveNext())
                        return string.Empty;

                    // We called MoveNext once, so this will be the first item
                    T currentValue = en.Current;

                    // Call ToString before calling MoveNext again, since
                    // we want to stay consistent with the below loop
                    // Everything should be called in the order
                    // MoveNext-Current-ToString, unless further optimizations
                    // can be made, to avoid breaking changes
                    string? firstString = currentValue?.ToString();

                    // If there's only 1 item, simply call ToString on that
                    if (!en.MoveNext())
                    {
                        // We have to handle the case of either currentValue
                        // or its ToString being null
                        return firstString ?? string.Empty;
                    }

                    var result = new ValueStringBuilder(stackalloc char[256]);

                    result.Append(firstString);

                    do
                    {
                        currentValue = en.Current;

                        if (currentValue != null)
                        {
                            result.Append(currentValue.ToString());
                        }
                    }
                    while (en.MoveNext());

                    return result.ToString();
                }
            }
        }

        public static string Concat(IEnumerable<string?> values)
        {
            if (values == null)
                throw new ArgumentNullException(nameof(values));

            using (IEnumerator<string?> en = values.GetEnumerator())
            {
                if (!en.MoveNext())
                    return string.Empty;

                string? firstValue = en.Current;

                if (!en.MoveNext())
                {
                    return firstValue ?? string.Empty;
                }

                var result = new ValueStringBuilder(stackalloc char[256]);

                result.Append(firstValue);

                do
                {
                    result.Append(en.Current);
                }
                while (en.MoveNext());

                return result.ToString();
            }
        }

        public static string Concat(string? str0, string? str1)
        {
            if (IsNullOrEmpty(str0))
            {
                if (IsNullOrEmpty(str1))
                {
                    return string.Empty;
                }
                return str1;
            }

            if (IsNullOrEmpty(str1))
            {
                return str0;
            }

            int str0Length = str0.Length;

            string result = FastAllocateString(str0Length + str1.Length);

            FillStringChecked(result, 0, str0);
            FillStringChecked(result, str0Length, str1);

            return result;
        }

        public static string Concat(string? str0, string? str1, string? str2)
        {
            if (IsNullOrEmpty(str0))
            {
                return Concat(str1, str2);
            }

            if (IsNullOrEmpty(str1))
            {
                return Concat(str0, str2);
            }

            if (IsNullOrEmpty(str2))
            {
                return Concat(str0, str1);
            }

            int totalLength = str0.Length + str1.Length + str2.Length;

            string result = FastAllocateString(totalLength);
            FillStringChecked(result, 0, str0);
            FillStringChecked(result, str0.Length, str1);
            FillStringChecked(result, str0.Length + str1.Length, str2);

            return result;
        }

        public static string Concat(string? str0, string? str1, string? str2, string? str3)
        {
            if (IsNullOrEmpty(str0))
            {
                return Concat(str1, str2, str3);
            }

            if (IsNullOrEmpty(str1))
            {
                return Concat(str0, str2, str3);
            }

            if (IsNullOrEmpty(str2))
            {
                return Concat(str0, str1, str3);
            }

            if (IsNullOrEmpty(str3))
            {
                return Concat(str0, str1, str2);
            }

            int totalLength = str0.Length + str1.Length + str2.Length + str3.Length;

            string result = FastAllocateString(totalLength);
            FillStringChecked(result, 0, str0);
            FillStringChecked(result, str0.Length, str1);
            FillStringChecked(result, str0.Length + str1.Length, str2);
            FillStringChecked(result, str0.Length + str1.Length + str2.Length, str3);

            return result;
        }

        public static string Concat(ReadOnlySpan<char> str0, ReadOnlySpan<char> str1)
        {
            int length = checked(str0.Length + str1.Length);
            if (length == 0)
            {
                return Empty;
            }

            string result = FastAllocateString(length);
            Span<char> resultSpan = new Span<char>(ref result.GetRawStringData(), result.Length);

            str0.CopyTo(resultSpan);
            str1.CopyTo(resultSpan.Slice(str0.Length));

            return result;
        }

        public static string Concat(ReadOnlySpan<char> str0, ReadOnlySpan<char> str1, ReadOnlySpan<char> str2)
        {
            int length = checked(str0.Length + str1.Length + str2.Length);
            if (length == 0)
            {
                return Empty;
            }

            string result = FastAllocateString(length);
            Span<char> resultSpan = new Span<char>(ref result.GetRawStringData(), result.Length);

            str0.CopyTo(resultSpan);
            resultSpan = resultSpan.Slice(str0.Length);

            str1.CopyTo(resultSpan);
            resultSpan = resultSpan.Slice(str1.Length);

            str2.CopyTo(resultSpan);

            return result;
        }

        public static string Concat(ReadOnlySpan<char> str0, ReadOnlySpan<char> str1, ReadOnlySpan<char> str2, ReadOnlySpan<char> str3)
        {
            int length = checked(str0.Length + str1.Length + str2.Length + str3.Length);
            if (length == 0)
            {
                return Empty;
            }

            string result = FastAllocateString(length);
            Span<char> resultSpan = new Span<char>(ref result.GetRawStringData(), result.Length);

            str0.CopyTo(resultSpan);
            resultSpan = resultSpan.Slice(str0.Length);

            str1.CopyTo(resultSpan);
            resultSpan = resultSpan.Slice(str1.Length);

            str2.CopyTo(resultSpan);
            resultSpan = resultSpan.Slice(str2.Length);

            str3.CopyTo(resultSpan);

            return result;
        }

        public static string Concat(params string?[] values)
        {
            if (values == null)
                throw new ArgumentNullException(nameof(values));

            if (values.Length <= 1)
            {
                return values.Length == 0 ?
                    string.Empty :
                    values[0] ?? string.Empty;
            }

            // It's possible that the input values array could be changed concurrently on another
            // thread, such that we can't trust that each read of values[i] will be equivalent.
            // Worst case, we can make a defensive copy of the array and use that, but we first
            // optimistically try the allocation and copies assuming that the array isn't changing,
            // which represents the 99.999% case, in particular since string.Concat is used for
            // string concatenation by the languages, with the input array being a params array.

            // Sum the lengths of all input strings
            long totalLengthLong = 0;
            for (int i = 0; i < values.Length; i++)
            {
                string? value = values[i];
                if (value != null)
                {
                    totalLengthLong += value.Length;
                }
            }

            // If it's too long, fail, or if it's empty, return an empty string.
            if (totalLengthLong > int.MaxValue)
            {
                throw new OutOfMemoryException();
            }
            int totalLength = (int)totalLengthLong;
            if (totalLength == 0)
            {
                return string.Empty;
            }

            // Allocate a new string and copy each input string into it
            string result = FastAllocateString(totalLength);
            int copiedLength = 0;
            for (int i = 0; i < values.Length; i++)
            {
                string? value = values[i];
                if (!string.IsNullOrEmpty(value))
                {
                    int valueLen = value.Length;
                    if (valueLen > totalLength - copiedLength)
                    {
                        copiedLength = -1;
                        break;
                    }

                    FillStringChecked(result, copiedLength, value);
                    copiedLength += valueLen;
                }
            }

            // If we copied exactly the right amount, return the new string.  Otherwise,
            // something changed concurrently to mutate the input array: fall back to
            // doing the concatenation again, but this time with a defensive copy. This
            // fall back should be extremely rare.
            return copiedLength == totalLength ? result : Concat((string?[])values.Clone());
        }

        public static string Format(string format, object? arg0)
        {
            return FormatHelper(null, format, new ParamsArray(arg0));
        }

        public static string Format(string format, object? arg0, object? arg1)
        {
            return FormatHelper(null, format, new ParamsArray(arg0, arg1));
        }

        public static string Format(string format, object? arg0, object? arg1, object? arg2)
        {
            return FormatHelper(null, format, new ParamsArray(arg0, arg1, arg2));
        }

        public static string Format(string format, params object?[] args)
        {
            if (args == null)
            {
                // To preserve the original exception behavior, throw an exception about format if both
                // args and format are null. The actual null check for format is in FormatHelper.
                throw new ArgumentNullException((format == null) ? nameof(format) : nameof(args));
            }

            return FormatHelper(null, format, new ParamsArray(args));
        }

        public static string Format(IFormatProvider? provider, string format, object? arg0)
        {
            return FormatHelper(provider, format, new ParamsArray(arg0));
        }

        public static string Format(IFormatProvider? provider, string format, object? arg0, object? arg1)
        {
            return FormatHelper(provider, format, new ParamsArray(arg0, arg1));
        }

        public static string Format(IFormatProvider? provider, string format, object? arg0, object? arg1, object? arg2)
        {
            return FormatHelper(provider, format, new ParamsArray(arg0, arg1, arg2));
        }

        public static string Format(IFormatProvider? provider, string format, params object?[] args)
        {
            if (args == null)
            {
                // To preserve the original exception behavior, throw an exception about format if both
                // args and format are null. The actual null check for format is in FormatHelper.
                throw new ArgumentNullException((format == null) ? nameof(format) : nameof(args));
            }

            return FormatHelper(provider, format, new ParamsArray(args));
        }

        private static string FormatHelper(IFormatProvider? provider, string format, ParamsArray args)
        {
            if (format == null)
                throw new ArgumentNullException(nameof(format));

            var sb = new ValueStringBuilder(stackalloc char[256]);
            sb.EnsureCapacity(format.Length + args.Length * 8);
            sb.AppendFormatHelper(provider, format, args);
            return sb.ToString();
        }

        public string Insert(int startIndex, string value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (startIndex < 0 || startIndex > this.Length)
                throw new ArgumentOutOfRangeException(nameof(startIndex));

            int oldLength = Length;
            int insertLength = value.Length;

            if (oldLength == 0)
                return value;
            if (insertLength == 0)
                return this;

            // In case this computation overflows, newLength will be negative and FastAllocateString throws OutOfMemoryException
            int newLength = oldLength + insertLength;
            string result = FastAllocateString(newLength);
            unsafe
            {
                fixed (char* srcThis = &_firstChar)
                {
                    fixed (char* srcInsert = &value._firstChar)
                    {
                        fixed (char* dst = &result._firstChar)
                        {
                            wstrcpy(dst, srcThis, startIndex);
                            wstrcpy(dst + startIndex, srcInsert, insertLength);
                            wstrcpy(dst + startIndex + insertLength, srcThis + startIndex, oldLength - startIndex);
                        }
                    }
                }
            }
            return result;
        }

        public static string Join(char separator, params string?[] value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            return Join(separator, value, 0, value.Length);
        }

        public static unsafe string Join(char separator, params object?[] values)
        {
            // Defer argument validation to the internal function
            return JoinCore(&separator, 1, values);
        }

        public static unsafe string Join<T>(char separator, IEnumerable<T> values)
        {
            // Defer argument validation to the internal function
            return JoinCore(&separator, 1, values);
        }

        public static unsafe string Join(char separator, string?[] value, int startIndex, int count)
        {
            // Defer argument validation to the internal function
            return JoinCore(&separator, 1, value, startIndex, count);
        }

        // Joins an array of strings together as one string with a separator between each original string.
        //
        public static string Join(string? separator, params string?[] value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            return Join(separator, value, 0, value.Length);
        }

        public static unsafe string Join(string? separator, params object?[] values)
        {
            separator ??= string.Empty;
            fixed (char* pSeparator = &separator._firstChar)
            {
                // Defer argument validation to the internal function
                return JoinCore(pSeparator, separator.Length, values);
            }
        }

        public static unsafe string Join<T>(string? separator, IEnumerable<T> values)
        {
            separator ??= string.Empty;
            fixed (char* pSeparator = &separator._firstChar)
            {
                // Defer argument validation to the internal function
                return JoinCore(pSeparator, separator.Length, values);
            }
        }

        public static string Join(string? separator, IEnumerable<string?> values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            using (IEnumerator<string?> en = values.GetEnumerator())
            {
                if (!en.MoveNext())
                {
                    return string.Empty;
                }

                string? firstValue = en.Current;

                if (!en.MoveNext())
                {
                    // Only one value available
                    return firstValue ?? string.Empty;
                }

                // Null separator and values are handled by the StringBuilder
                var result = new ValueStringBuilder(stackalloc char[256]);

                result.Append(firstValue);

                do
                {
                    result.Append(separator);
                    result.Append(en.Current);
                }
                while (en.MoveNext());

                return result.ToString();
            }
        }

        // Joins an array of strings together as one string with a separator between each original string.
        //
        public static unsafe string Join(string? separator, string?[] value, int startIndex, int count)
        {
            separator ??= Empty;
            fixed (char* pSeparator = &separator._firstChar)
            {
                // Defer argument validation to the internal function
                return JoinCore(pSeparator, separator.Length, value, startIndex, count);
            }
        }

        private static unsafe string JoinCore(char* separator, int separatorLength, object?[] values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            if (values.Length == 0)
            {
                return string.Empty;
            }

            string? firstString = values[0]?.ToString();

            if (values.Length == 1)
            {
                return firstString ?? string.Empty;
            }

            var result = new ValueStringBuilder(stackalloc char[256]);

            result.Append(firstString);

            for (int i = 1; i < values.Length; i++)
            {
                result.Append(separator, separatorLength);
                object? value = values[i];
                if (value != null)
                {
                    result.Append(value.ToString());
                }
            }

            return result.ToString();
        }

        private static unsafe string JoinCore<T>(char* separator, int separatorLength, IEnumerable<T> values)
        {
            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            using (IEnumerator<T> en = values.GetEnumerator())
            {
                if (!en.MoveNext())
                {
                    return string.Empty;
                }

                // We called MoveNext once, so this will be the first item
                T currentValue = en.Current;

                // Call ToString before calling MoveNext again, since
                // we want to stay consistent with the below loop
                // Everything should be called in the order
                // MoveNext-Current-ToString, unless further optimizations
                // can be made, to avoid breaking changes
                string? firstString = currentValue?.ToString();

                // If there's only 1 item, simply call ToString on that
                if (!en.MoveNext())
                {
                    // We have to handle the case of either currentValue
                    // or its ToString being null
                    return firstString ?? string.Empty;
                }

                var result = new ValueStringBuilder(stackalloc char[256]);

                result.Append(firstString);

                do
                {
                    currentValue = en.Current;

                    result.Append(separator, separatorLength);
                    if (currentValue != null)
                    {
                        result.Append(currentValue.ToString());
                    }
                }
                while (en.MoveNext());

                return result.ToString();
            }
        }

        private static unsafe string JoinCore(char* separator, int separatorLength, string?[] value, int startIndex, int count)
        {
            // If the separator is null, it is converted to an empty string before entering this function.
            // Even for empty strings, fixed should never return null (it should return a pointer to a null char).
            Debug.Assert(separator != null);
            Debug.Assert(separatorLength >= 0);

            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            if (startIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex), SR.ArgumentOutOfRange_StartIndex);
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count), SR.ArgumentOutOfRange_NegativeCount);
            }
            if (startIndex > value.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex), SR.ArgumentOutOfRange_IndexCountBuffer);
            }

            if (count <= 1)
            {
                return count == 0 ?
                    string.Empty :
                    value[startIndex] ?? string.Empty;
            }

            long totalSeparatorsLength = (long)(count - 1) * separatorLength;
            if (totalSeparatorsLength > int.MaxValue)
            {
                throw new OutOfMemoryException();
            }
            int totalLength = (int)totalSeparatorsLength;

            // Calculate the length of the resultant string so we know how much space to allocate.
            for (int i = startIndex, end = startIndex + count; i < end; i++)
            {
                string? currentValue = value[i];
                if (currentValue != null)
                {
                    totalLength += currentValue.Length;
                    if (totalLength < 0) // Check for overflow
                    {
                        throw new OutOfMemoryException();
                    }
                }
            }

            // Copy each of the strings into the resultant buffer, interleaving with the separator.
            string result = FastAllocateString(totalLength);
            int copiedLength = 0;

            for (int i = startIndex, end = startIndex + count; i < end; i++)
            {
                // It's possible that another thread may have mutated the input array
                // such that our second read of an index will not be the same string
                // we got during the first read.

                // We range check again to avoid buffer overflows if this happens.

                string? currentValue = value[i];
                if (currentValue != null)
                {
                    int valueLen = currentValue.Length;
                    if (valueLen > totalLength - copiedLength)
                    {
                        copiedLength = -1;
                        break;
                    }

                    // Fill in the value.
                    FillStringChecked(result, copiedLength, currentValue);
                    copiedLength += valueLen;
                }

                if (i < end - 1)
                {
                    // Fill in the separator.
                    fixed (char* pResult = &result._firstChar)
                    {
                        // If we are called from the char-based overload, we will not
                        // want to call MemoryCopy each time we fill in the separator. So
                        // specialize for 1-length separators.
                        if (separatorLength == 1)
                        {
                            pResult[copiedLength] = *separator;
                        }
                        else
                        {
                            wstrcpy(pResult + copiedLength, separator, separatorLength);
                        }
                    }
                    copiedLength += separatorLength;
                }
            }

            // If we copied exactly the right amount, return the new string.  Otherwise,
            // something changed concurrently to mutate the input array: fall back to
            // doing the concatenation again, but this time with a defensive copy. This
            // fall back should be extremely rare.
            return copiedLength == totalLength ?
                result :
                JoinCore(separator, separatorLength, (string?[])value.Clone(), startIndex, count);
        }

        public string PadLeft(int totalWidth) => PadLeft(totalWidth, ' ');

        public string PadLeft(int totalWidth, char paddingChar)
        {
            if (totalWidth < 0)
                throw new ArgumentOutOfRangeException(nameof(totalWidth), SR.ArgumentOutOfRange_NeedNonNegNum);
            int oldLength = Length;
            int count = totalWidth - oldLength;
            if (count <= 0)
                return this;
            string result = FastAllocateString(totalWidth);
            unsafe
            {
                fixed (char* dst = &result._firstChar)
                {
                    for (int i = 0; i < count; i++)
                        dst[i] = paddingChar;
                    fixed (char* src = &_firstChar)
                    {
                        wstrcpy(dst + count, src, oldLength);
                    }
                }
            }
            return result;
        }

        public string PadRight(int totalWidth) => PadRight(totalWidth, ' ');

        public string PadRight(int totalWidth, char paddingChar)
        {
            if (totalWidth < 0)
                throw new ArgumentOutOfRangeException(nameof(totalWidth), SR.ArgumentOutOfRange_NeedNonNegNum);
            int oldLength = Length;
            int count = totalWidth - oldLength;
            if (count <= 0)
                return this;
            string result = FastAllocateString(totalWidth);
            unsafe
            {
                fixed (char* dst = &result._firstChar)
                {
                    fixed (char* src = &_firstChar)
                    {
                        wstrcpy(dst, src, oldLength);
                    }
                    for (int i = 0; i < count; i++)
                        dst[oldLength + i] = paddingChar;
                }
            }
            return result;
        }

        public string Remove(int startIndex, int count)
        {
            if (startIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(startIndex), SR.ArgumentOutOfRange_StartIndex);
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), SR.ArgumentOutOfRange_NegativeCount);
            int oldLength = this.Length;
            if (count > oldLength - startIndex)
                throw new ArgumentOutOfRangeException(nameof(count), SR.ArgumentOutOfRange_IndexCount);

            if (count == 0)
                return this;
            int newLength = oldLength - count;
            if (newLength == 0)
                return string.Empty;

            string result = FastAllocateString(newLength);
            unsafe
            {
                fixed (char* src = &_firstChar)
                {
                    fixed (char* dst = &result._firstChar)
                    {
                        wstrcpy(dst, src, startIndex);
                        wstrcpy(dst + startIndex, src + startIndex + count, newLength - startIndex);
                    }
                }
            }
            return result;
        }

        // a remove that just takes a startindex.
        public string Remove(int startIndex)
        {
            if (startIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(startIndex), SR.ArgumentOutOfRange_StartIndex);

            if (startIndex >= Length)
                throw new ArgumentOutOfRangeException(nameof(startIndex), SR.ArgumentOutOfRange_StartIndexLessThanLength);

            return Substring(0, startIndex);
        }

        public string Replace(string oldValue, string? newValue, bool ignoreCase, CultureInfo? culture)
        {
            return ReplaceCore(oldValue, newValue, culture?.CompareInfo, ignoreCase ? CompareOptions.IgnoreCase : CompareOptions.None);
        }

        public string Replace(string oldValue, string? newValue, StringComparison comparisonType)
        {
            switch (comparisonType)
            {
                case StringComparison.CurrentCulture:
                case StringComparison.CurrentCultureIgnoreCase:
                    return ReplaceCore(oldValue, newValue, CultureInfo.CurrentCulture.CompareInfo, GetCaseCompareOfComparisonCulture(comparisonType));

                case StringComparison.InvariantCulture:
                case StringComparison.InvariantCultureIgnoreCase:
                    return ReplaceCore(oldValue, newValue, CompareInfo.Invariant, GetCaseCompareOfComparisonCulture(comparisonType));

                case StringComparison.Ordinal:
                    return Replace(oldValue, newValue);

                case StringComparison.OrdinalIgnoreCase:
                    return ReplaceCore(oldValue, newValue, CompareInfo.Invariant, CompareOptions.OrdinalIgnoreCase);

                default:
                    throw new ArgumentException(SR.NotSupported_StringComparison, nameof(comparisonType));
            }
        }

        private string ReplaceCore(string oldValue, string? newValue, CompareInfo? ci, CompareOptions options)
        {
            if (oldValue is null)
            {
                throw new ArgumentNullException(nameof(oldValue));
            }

            if (oldValue.Length == 0)
            {
                throw new ArgumentException(SR.Argument_StringZeroLength, nameof(oldValue));
            }

            // If they asked to replace oldValue with a null, replace all occurrences
            // with the empty string. AsSpan() will normalize appropriately.
            //
            // If inner ReplaceCore method returns null, it means no substitutions were
            // performed, so as an optimization we'll return the original string.

            return ReplaceCore(this, oldValue.AsSpan(), newValue.AsSpan(), ci ?? CultureInfo.CurrentCulture.CompareInfo, options)
                ?? this;
        }

        private static unsafe string? ReplaceCore(ReadOnlySpan<char> searchSpace, ReadOnlySpan<char> oldValue, ReadOnlySpan<char> newValue, CompareInfo compareInfo, CompareOptions options)
        {
            Debug.Assert(!oldValue.IsEmpty);
            Debug.Assert(compareInfo != null);

            var result = new ValueStringBuilder(stackalloc char[256]);
            result.EnsureCapacity(searchSpace.Length);

            int matchLength = 0;
            bool hasDoneAnyReplacements = false;

            while (true)
            {
                int index = compareInfo.IndexOf(searchSpace, oldValue, &matchLength, options, fromBeginning: true);

                // There's the possibility that 'oldValue' has zero collation weight (empty string equivalent).
                // If this is the case, we behave as if there are no more substitutions to be made.

                if (index < 0 || matchLength == 0)
                {
                    break;
                }

                // append the unmodified portion of search space
                result.Append(searchSpace.Slice(0, index));

                // append the replacement
                result.Append(newValue);

                searchSpace = searchSpace.Slice(index + matchLength);
                hasDoneAnyReplacements = true;
            }

            // Didn't find 'oldValue' in the remaining search space, or the match
            // consisted only of zero collation weight characters. As an optimization,
            // if we have not yet performed any replacements, we'll save the
            // allocation.

            if (!hasDoneAnyReplacements)
            {
                result.Dispose();
                return null;
            }

            // Append what remains of the search space, then allocate the new string.

            result.Append(searchSpace);
            return result.ToString();
        }

        // Replaces all instances of oldChar with newChar.
        //
        public string Replace(char oldChar, char newChar)
        {
            if (oldChar == newChar)
                return this;

            int firstIndex = IndexOf(oldChar);

            if (firstIndex < 0)
                return this;

            int remainingLength = Length - firstIndex;
            string result = FastAllocateString(Length);

            int copyLength = firstIndex;

            // Copy the characters already proven not to match.
            if (copyLength > 0)
            {
                Buffer.Memmove(ref result._firstChar, ref _firstChar, (uint)copyLength);
            }

            // Copy the remaining characters, doing the replacement as we go.
            ref ushort pSrc = ref Unsafe.Add(ref Unsafe.As<char, ushort>(ref _firstChar), copyLength);
            ref ushort pDst = ref Unsafe.Add(ref Unsafe.As<char, ushort>(ref result._firstChar), copyLength);

            if (Vector.IsHardwareAccelerated && remainingLength >= Vector<ushort>.Count)
            {
                Vector<ushort> oldChars = new Vector<ushort>(oldChar);
                Vector<ushort> newChars = new Vector<ushort>(newChar);

                do
                {
                    Vector<ushort> original = Unsafe.ReadUnaligned<Vector<ushort>>(ref Unsafe.As<ushort, byte>(ref pSrc));
                    Vector<ushort> equals = Vector.Equals(original, oldChars);
                    Vector<ushort> results = Vector.ConditionalSelect(equals, newChars, original);
                    Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref pDst), results);

                    pSrc = ref Unsafe.Add(ref pSrc, Vector<ushort>.Count);
                    pDst = ref Unsafe.Add(ref pDst, Vector<ushort>.Count);
                    remainingLength -= Vector<ushort>.Count;
                }
                while (remainingLength >= Vector<ushort>.Count);
            }

            for (; remainingLength > 0; remainingLength--)
            {
                ushort currentChar = pSrc;
                pDst = currentChar == oldChar ? newChar : currentChar;

                pSrc = ref Unsafe.Add(ref pSrc, 1);
                pDst = ref Unsafe.Add(ref pDst, 1);
            }

            return result;
        }

        public string Replace(string oldValue, string? newValue)
        {
            if (oldValue == null)
                throw new ArgumentNullException(nameof(oldValue));
            if (oldValue.Length == 0)
                throw new ArgumentException(SR.Argument_StringZeroLength, nameof(oldValue));

            // Api behavior: if newValue is null, instances of oldValue are to be removed.
            newValue ??= string.Empty;

            var replacementIndices = new ValueListBuilder<int>(stackalloc int[StackallocIntBufferSizeLimit]);

            unsafe
            {
                fixed (char* pThis = &_firstChar)
                {
                    int matchIdx = 0;
                    int lastPossibleMatchIdx = this.Length - oldValue.Length;
                    while (matchIdx <= lastPossibleMatchIdx)
                    {
                        char* pMatch = pThis + matchIdx;
                        for (int probeIdx = 0; probeIdx < oldValue.Length; probeIdx++)
                        {
                            if (pMatch[probeIdx] != oldValue[probeIdx])
                            {
                                goto Next;
                            }
                        }
                        // Found a match for the string. Record the location of the match and skip over the "oldValue."
                        replacementIndices.Append(matchIdx);
                        matchIdx += oldValue.Length;
                        continue;

                    Next:
                        matchIdx++;
                    }
                }
            }

            if (replacementIndices.Length == 0)
                return this;

            // String allocation and copying is in separate method to make this method faster for the case where
            // nothing needs replacing.
            string dst = ReplaceHelper(oldValue.Length, newValue, replacementIndices.AsSpan());

            replacementIndices.Dispose();

            return dst;
        }

        private string ReplaceHelper(int oldValueLength, string newValue, ReadOnlySpan<int> indices)
        {
            Debug.Assert(indices.Length > 0);

            long dstLength = this.Length + ((long)(newValue.Length - oldValueLength)) * indices.Length;
            if (dstLength > int.MaxValue)
                throw new OutOfMemoryException();
            string dst = FastAllocateString((int)dstLength);

            Span<char> dstSpan = new Span<char>(ref dst.GetRawStringData(), dst.Length);

            int thisIdx = 0;
            int dstIdx = 0;

            for (int r = 0; r < indices.Length; r++)
            {
                int replacementIdx = indices[r];

                // Copy over the non-matching portion of the original that precedes this occurrence of oldValue.
                int count = replacementIdx - thisIdx;
                if (count != 0)
                {
                    this.AsSpan(thisIdx, count).CopyTo(dstSpan.Slice(dstIdx));
                    dstIdx += count;
                }
                thisIdx = replacementIdx + oldValueLength;

                // Copy over newValue to replace the oldValue.
                newValue.AsSpan().CopyTo(dstSpan.Slice(dstIdx));
                dstIdx += newValue.Length;
            }

            // Copy over the final non-matching portion at the end of the string.
            Debug.Assert(this.Length - thisIdx == dstSpan.Length - dstIdx);
            this.AsSpan(thisIdx).CopyTo(dstSpan.Slice(dstIdx));

            return dst;
        }

        public string[] Split(char separator, StringSplitOptions options = StringSplitOptions.None)
        {
            return SplitInternal(new ReadOnlySpan<char>(ref separator, 1), int.MaxValue, options);
        }

        public string[] Split(char separator, int count, StringSplitOptions options = StringSplitOptions.None)
        {
            return SplitInternal(new ReadOnlySpan<char>(ref separator, 1), count, options);
        }

        // Creates an array of strings by splitting this string at each
        // occurrence of a separator.  The separator is searched for, and if found,
        // the substring preceding the occurrence is stored as the first element in
        // the array of strings.  We then continue in this manner by searching
        // the substring that follows the occurrence.  On the other hand, if the separator
        // is not found, the array of strings will contain this instance as its only element.
        // If the separator is null
        // whitespace (i.e., Character.IsWhitespace) is used as the separator.
        //
        public string[] Split(params char[]? separator)
        {
            return SplitInternal(separator, int.MaxValue, StringSplitOptions.None);
        }

        // Creates an array of strings by splitting this string at each
        // occurrence of a separator.  The separator is searched for, and if found,
        // the substring preceding the occurrence is stored as the first element in
        // the array of strings.  We then continue in this manner by searching
        // the substring that follows the occurrence.  On the other hand, if the separator
        // is not found, the array of strings will contain this instance as its only element.
        // If the separator is the empty string (i.e., string.Empty), then
        // whitespace (i.e., Character.IsWhitespace) is used as the separator.
        // If there are more than count different strings, the last n-(count-1)
        // elements are concatenated and added as the last string.
        //
        public string[] Split(char[]? separator, int count)
        {
            return SplitInternal(separator, count, StringSplitOptions.None);
        }

        public string[] Split(char[]? separator, StringSplitOptions options)
        {
            return SplitInternal(separator, int.MaxValue, options);
        }

        public string[] Split(char[]? separator, int count, StringSplitOptions options)
        {
            return SplitInternal(separator, count, options);
        }

        private string[] SplitInternal(ReadOnlySpan<char> separators, int count, StringSplitOptions options)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count),
                    SR.ArgumentOutOfRange_NegativeCount);

            CheckStringSplitOptions(options);

        ShortCircuit:
            if (count <= 1 || Length == 0)
            {
                // Per the method's documentation, we'll short-circuit the search for separators.
                // But we still need to post-process the results based on the caller-provided flags.

                string candidate = this;
                if (((options & StringSplitOptions.TrimEntries) != 0) && (count > 0))
                {
                    candidate = candidate.Trim();
                }
                if (((options & StringSplitOptions.RemoveEmptyEntries) != 0) && (candidate.Length == 0))
                {
                    count = 0;
                }
                return (count == 0) ? Array.Empty<string>() : new string[] { candidate };
            }

            if (separators.IsEmpty)
            {
                // Caller is already splitting on whitespace; no need for separate trim step
                options &= ~StringSplitOptions.TrimEntries;
            }

            var sepListBuilder = new ValueListBuilder<int>(stackalloc int[StackallocIntBufferSizeLimit]);

            MakeSeparatorList(separators, ref sepListBuilder);
            ReadOnlySpan<int> sepList = sepListBuilder.AsSpan();

            // Handle the special case of no replaces.
            if (sepList.Length == 0)
            {
                count = 1;
                goto ShortCircuit;
            }

            string[] result = (options != StringSplitOptions.None)
                ? SplitWithPostProcessing(sepList, default, 1, count, options)
                : SplitWithoutPostProcessing(sepList, default, 1, count);

            sepListBuilder.Dispose();

            return result;
        }

        public string[] Split(string? separator, StringSplitOptions options = StringSplitOptions.None)
        {
            return SplitInternal(separator ?? string.Empty, null, int.MaxValue, options);
        }

        public string[] Split(string? separator, int count, StringSplitOptions options = StringSplitOptions.None)
        {
            return SplitInternal(separator ?? string.Empty, null, count, options);
        }

        public string[] Split(string[]? separator, StringSplitOptions options)
        {
            return SplitInternal(null, separator, int.MaxValue, options);
        }

        public string[] Split(string[]? separator, int count, StringSplitOptions options)
        {
            return SplitInternal(null, separator, count, options);
        }

        private string[] SplitInternal(string? separator, string?[]? separators, int count, StringSplitOptions options)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count),
                    SR.ArgumentOutOfRange_NegativeCount);
            }

            CheckStringSplitOptions(options);

            bool singleSeparator = separator != null;

            if (!singleSeparator && (separators == null || separators.Length == 0))
            {
                // split on whitespace
                return SplitInternal(default(ReadOnlySpan<char>), count, options);
            }

        ShortCircuit:
            if (count <= 1 || Length == 0)
            {
                // Per the method's documentation, we'll short-circuit the search for separators.
                // But we still need to post-process the results based on the caller-provided flags.

                string candidate = this;
                if (((options & StringSplitOptions.TrimEntries) != 0) && (count > 0))
                {
                    candidate = candidate.Trim();
                }
                if (((options & StringSplitOptions.RemoveEmptyEntries) != 0) && (candidate.Length == 0))
                {
                    count = 0;
                }
                return (count == 0) ? Array.Empty<string>() : new string[] { candidate };
            }

            if (singleSeparator)
            {
                if (separator!.Length == 0)
                {
                    count = 1;
                    goto ShortCircuit;
                }
                else
                {
                    return SplitInternal(separator, count, options);
                }
            }

            var sepListBuilder = new ValueListBuilder<int>(stackalloc int[StackallocIntBufferSizeLimit]);
            var lengthListBuilder = new ValueListBuilder<int>(stackalloc int[StackallocIntBufferSizeLimit]);

            MakeSeparatorList(separators!, ref sepListBuilder, ref lengthListBuilder);
            ReadOnlySpan<int> sepList = sepListBuilder.AsSpan();
            ReadOnlySpan<int> lengthList = lengthListBuilder.AsSpan();

            // Handle the special case of no replaces.
            if (sepList.Length == 0)
            {
                return new string[] { this };
            }

            string[] result = (options != StringSplitOptions.None)
                ? SplitWithPostProcessing(sepList, lengthList, 0, count, options)
                : SplitWithoutPostProcessing(sepList, lengthList, 0, count);

            sepListBuilder.Dispose();
            lengthListBuilder.Dispose();

            return result;
        }

        private string[] SplitInternal(string separator, int count, StringSplitOptions options)
        {
            var sepListBuilder = new ValueListBuilder<int>(stackalloc int[StackallocIntBufferSizeLimit]);

            MakeSeparatorList(separator, ref sepListBuilder);
            ReadOnlySpan<int> sepList = sepListBuilder.AsSpan();
            if (sepList.Length == 0)
            {
                // there are no separators so sepListBuilder did not rent an array from pool and there is no need to dispose it
                string candidate = this;
                if ((options & StringSplitOptions.TrimEntries) != 0)
                {
                    candidate = candidate.Trim();
                }
                return ((candidate.Length == 0) && ((options & StringSplitOptions.RemoveEmptyEntries) != 0))
                    ? Array.Empty<string>()
                    : new string[] { candidate };
            }

            string[] result = (options != StringSplitOptions.None)
                ? SplitWithPostProcessing(sepList, default, separator.Length, count, options)
                : SplitWithoutPostProcessing(sepList, default, separator.Length, count);

            sepListBuilder.Dispose();

            return result;
        }

        // This function will not trim entries or special-case empty entries
        private string[] SplitWithoutPostProcessing(ReadOnlySpan<int> sepList, ReadOnlySpan<int> lengthList, int defaultLength, int count)
        {
            Debug.Assert(count >= 2);

            int currIndex = 0;
            int arrIndex = 0;

            count--;
            int numActualReplaces = (sepList.Length < count) ? sepList.Length : count;

            // Allocate space for the new array.
            // +1 for the string from the end of the last replace to the end of the string.
            string[] splitStrings = new string[numActualReplaces + 1];

            for (int i = 0; i < numActualReplaces && currIndex < Length; i++)
            {
                splitStrings[arrIndex++] = Substring(currIndex, sepList[i] - currIndex);
                currIndex = sepList[i] + (lengthList.IsEmpty ? defaultLength : lengthList[i]);
            }

            // Handle the last string at the end of the array if there is one.
            if (currIndex < Length && numActualReplaces >= 0)
            {
                splitStrings[arrIndex] = Substring(currIndex);
            }
            else if (arrIndex == numActualReplaces)
            {
                // We had a separator character at the end of a string.  Rather than just allowing
                // a null character, we'll replace the last element in the array with an empty string.
                splitStrings[arrIndex] = string.Empty;
            }

            return splitStrings;
        }


        // This function may trim entries or omit empty entries
        private string[] SplitWithPostProcessing(ReadOnlySpan<int> sepList, ReadOnlySpan<int> lengthList, int defaultLength, int count, StringSplitOptions options)
        {
            Debug.Assert(count >= 2);

            int numReplaces = sepList.Length;

            // Allocate array to hold items. This array may not be
            // filled completely in this function, we will create a
            // new array and copy string references to that new array.
            int maxItems = (numReplaces < count) ? (numReplaces + 1) : count;
            string[] splitStrings = new string[maxItems];

            int currIndex = 0;
            int arrIndex = 0;

            ReadOnlySpan<char> thisEntry;

            for (int i = 0; i < numReplaces; i++)
            {
                thisEntry = this.AsSpan(currIndex, sepList[i] - currIndex);
                if ((options & StringSplitOptions.TrimEntries) != 0)
                {
                    thisEntry = thisEntry.Trim();
                }
                if (!thisEntry.IsEmpty || ((options & StringSplitOptions.RemoveEmptyEntries) == 0))
                {
                    splitStrings[arrIndex++] = thisEntry.ToString();
                }
                currIndex = sepList[i] + (lengthList.IsEmpty ? defaultLength : lengthList[i]);
                if (arrIndex == count - 1)
                {
                    // The next iteration of the loop will provide the final entry into the
                    // results array. If needed, skip over all empty entries before that
                    // point.
                    if ((options & StringSplitOptions.RemoveEmptyEntries) != 0)
                    {
                        while (++i < numReplaces)
                        {
                            thisEntry = this.AsSpan(currIndex, sepList[i] - currIndex);
                            if ((options & StringSplitOptions.TrimEntries) != 0)
                            {
                                thisEntry = thisEntry.Trim();
                            }
                            if (!thisEntry.IsEmpty)
                            {
                                break; // there's useful data here
                            }
                            currIndex = sepList[i] + (lengthList.IsEmpty ? defaultLength : lengthList[i]);
                        }
                    }
                    break;
                }
            }

            // we must have at least one slot left to fill in the last string.
            Debug.Assert(arrIndex < maxItems);

            // Handle the last substring at the end of the array
            // (could be empty if separator appeared at the end of the input string)
            thisEntry = this.AsSpan(currIndex);
            if ((options & StringSplitOptions.TrimEntries) != 0)
            {
                thisEntry = thisEntry.Trim();
            }
            if (!thisEntry.IsEmpty || ((options & StringSplitOptions.RemoveEmptyEntries) == 0))
            {
                splitStrings[arrIndex++] = thisEntry.ToString();
            }

            Array.Resize(ref splitStrings, arrIndex);
            return splitStrings;
        }

        /// <summary>
        /// Uses ValueListBuilder to create list that holds indexes of separators in string.
        /// </summary>
        /// <param name="separators"><see cref="ReadOnlySpan{T}"/> of separator chars</param>
        /// <param name="sepListBuilder"><see cref="ValueListBuilder{T}"/> to store indexes</param>
        private void MakeSeparatorList(ReadOnlySpan<char> separators, ref ValueListBuilder<int> sepListBuilder)
        {
            char sep0, sep1, sep2;

            switch (separators.Length)
            {
                // Special-case no separators to mean any whitespace is a separator.
                case 0:
                    for (int i = 0; i < Length; i++)
                    {
                        if (char.IsWhiteSpace(this[i]))
                        {
                            sepListBuilder.Append(i);
                        }
                    }
                    break;

                // Special-case the common cases of 1, 2, and 3 separators, with manual comparisons against each separator.
                case 1:
                    sep0 = separators[0];

                    if (Avx2.IsSupported && Length >= 16)
                    {
                        MakeSeparatorListVectorized(ref sepListBuilder, sep0, sep0, sep0);
                        return;
                    }

                    for (int i = 0; i < Length; i++)
                    {
                        if (this[i] == sep0)
                        {
                            sepListBuilder.Append(i);
                        }
                    }
                    break;
                case 2:
                    sep0 = separators[0];
                    sep1 = separators[1];

                    if (Avx2.IsSupported && Length >= 16)
                    {
                        MakeSeparatorListVectorized(ref sepListBuilder, sep0, sep1, sep1);
                        return;
                    }

                    for (int i = 0; i < Length; i++)
                    {
                        char c = this[i];
                        if (c == sep0 || c == sep1)
                        {
                            sepListBuilder.Append(i);
                        }
                    }
                    break;
                case 3:
                    sep0 = separators[0];
                    sep1 = separators[1];
                    sep2 = separators[2];

                    if (Avx2.IsSupported && Length >= 16)
                    {
                        MakeSeparatorListVectorized(ref sepListBuilder, sep0, sep1, sep2);
                        return;
                    }

                    for (int i = 0; i < Length; i++)
                    {
                        char c = this[i];
                        if (c == sep0 || c == sep1 || c == sep2)
                        {
                            sepListBuilder.Append(i);
                        }
                    }
                    break;

                // Handle > 3 separators with a probabilistic map, ala IndexOfAny.
                // This optimizes for chars being unlikely to match a separator.
                default:
                    unsafe
                    {
                        ProbabilisticMap map = default;
                        uint* charMap = (uint*)&map;
                        InitializeProbabilisticMap(charMap, separators);

                        for (int i = 0; i < Length; i++)
                        {
                            char c = this[i];
                            if (IsCharBitSet(charMap, (byte)c) && IsCharBitSet(charMap, (byte)(c >> 8)) &&
                                separators.Contains(c))
                            {
                                sepListBuilder.Append(i);
                            }
                        }
                    }
                    break;
            }
        }

        private void MakeSeparatorListVectorized(ref ValueListBuilder<int> sepListBuilder, char c, char c2, char c3)
        {
            // Constant that allows for the truncation of 16-bit (FFFF/0000) values within a register to 4-bit (F/0)
            Vector256<byte> shuffleConstant = Vector256.Create(0x02, 0x06, 0x0A, 0x0E, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                                                               0xFF, 0xFF, 0xFF, 0xFF, 0x02, 0x06, 0x0A, 0x0E, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);

            Vector256<ushort> v1 = Vector256.Create(c);
            Vector256<ushort> v2 = c2 is char sep2 ? Vector256.Create(sep2) : v1;
            Vector256<ushort> v3 = c3 is char sep3 ? Vector256.Create(sep3) : v2;

            ref char c0 = ref MemoryMarshal.GetReference(this.AsSpan());
            int cond = Length & -Vector256<ushort>.Count;
            int i = 0;

            for (; i < cond; i += Vector256<ushort>.Count)
            {
                Vector256<ushort> charVector = ReadVector(ref c0, i);
                Vector256<ushort> cmp = Avx2.CompareEqual(charVector, v1);

                cmp = Avx2.Or(Avx2.CompareEqual(charVector, v2), cmp);
                cmp = Avx2.Or(Avx2.CompareEqual(charVector, v3), cmp);

                if (Avx.TestZ(cmp, cmp)) { continue; }

                Vector256<byte> mask = Avx2.ShiftLeftLogical(cmp.AsUInt64(), 4).AsByte();
                mask = Avx2.Shuffle(mask, shuffleConstant);

                Vector128<byte> res = Sse2.Or(mask.GetLower(), mask.GetUpper());

                // Constant that defines indices of characters within an AVX-Register
                const ulong indicesConstant = 0xFEDCBA9876543210;
                ulong extractedBits = Bmi2.X64.ParallelBitExtract(indicesConstant, Sse2.X64.ConvertToUInt64(res.AsUInt64()));

                while (true)
                {
                    sepListBuilder.Append(((byte)(extractedBits & 0xF)) + i);
                    extractedBits >>= 4;
                    if (extractedBits == 0) { break; }
                }
            }

            for (; i < Length; i++)
            {
                char curr = Unsafe.Add(ref c0, (IntPtr)(uint)i);
                if (curr == c || curr == c2 || curr == c3)
                {
                    sepListBuilder.Append(i);
                }
            }

            static Vector256<ushort> ReadVector(ref char c0, int offset)
            {
                ref char ci = ref Unsafe.Add(ref c0, (IntPtr)(uint)offset);
                ref byte b = ref Unsafe.As<char, byte>(ref ci);
                return Unsafe.ReadUnaligned<Vector256<ushort>>(ref b);
            }
        }

        /// <summary>
        /// Uses ValueListBuilder to create list that holds indexes of separators in string.
        /// </summary>
        /// <param name="separator">separator string</param>
        /// <param name="sepListBuilder"><see cref="ValueListBuilder{T}"/> to store indexes</param>
        private void MakeSeparatorList(string separator, ref ValueListBuilder<int> sepListBuilder)
        {
            Debug.Assert(!IsNullOrEmpty(separator), "!string.IsNullOrEmpty(separator)");

            int currentSepLength = separator.Length;

            for (int i = 0; i < Length; i++)
            {
                if (this[i] == separator[0] && currentSepLength <= Length - i)
                {
                    if (currentSepLength == 1
                        || this.AsSpan(i, currentSepLength).SequenceEqual(separator))
                    {
                        sepListBuilder.Append(i);
                        i += currentSepLength - 1;
                    }
                }
            }
        }

        /// <summary>
        /// Uses ValueListBuilder to create list that holds indexes of separators in string and list that holds length of separator strings.
        /// </summary>
        /// <param name="separators">separator strngs</param>
        /// <param name="sepListBuilder"><see cref="ValueListBuilder{T}"/> for separator indexes</param>
        /// <param name="lengthListBuilder"><see cref="ValueListBuilder{T}"/> for separator length values</param>
        private void MakeSeparatorList(string?[] separators, ref ValueListBuilder<int> sepListBuilder, ref ValueListBuilder<int> lengthListBuilder)
        {
            Debug.Assert(separators != null && separators.Length > 0, "separators != null && separators.Length > 0");

            for (int i = 0; i < Length; i++)
            {
                for (int j = 0; j < separators.Length; j++)
                {
                    string? separator = separators[j];
                    if (IsNullOrEmpty(separator))
                    {
                        continue;
                    }
                    int currentSepLength = separator.Length;
                    if (this[i] == separator[0] && currentSepLength <= Length - i)
                    {
                        if (currentSepLength == 1
                            || this.AsSpan(i, currentSepLength).SequenceEqual(separator))
                        {
                            sepListBuilder.Append(i);
                            lengthListBuilder.Append(currentSepLength);
                            i += currentSepLength - 1;
                            break;
                        }
                    }
                }
            }
        }

        private static void CheckStringSplitOptions(StringSplitOptions options)
        {
            const StringSplitOptions AllValidFlags = StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries;

            if ((options & ~AllValidFlags) != 0)
            {
                // at least one invalid flag was set
                ThrowHelper.ThrowArgumentException(ExceptionResource.Argument_InvalidFlag, ExceptionArgument.options);
            }
        }

        // Returns a substring of this string.
        //
        public string Substring(int startIndex) => Substring(startIndex, Length - startIndex);

        public string Substring(int startIndex, int length)
        {
            if (startIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex), SR.ArgumentOutOfRange_StartIndex);
            }

            if (startIndex > Length)
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex), SR.ArgumentOutOfRange_StartIndexLargerThanLength);
            }

            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length), SR.ArgumentOutOfRange_NegativeLength);
            }

            if (startIndex > Length - length)
            {
                throw new ArgumentOutOfRangeException(nameof(length), SR.ArgumentOutOfRange_IndexLength);
            }

            if (length == 0)
            {
                return string.Empty;
            }

            if (startIndex == 0 && length == this.Length)
            {
                return this;
            }

            return InternalSubString(startIndex, length);
        }

        private string InternalSubString(int startIndex, int length)
        {
            Debug.Assert(startIndex >= 0 && startIndex <= this.Length, "StartIndex is out of range!");
            Debug.Assert(length >= 0 && startIndex <= this.Length - length, "length is out of range!");

            string result = FastAllocateString(length);

            Buffer.Memmove(
                elementCount: (uint)result.Length, // derefing Length now allows JIT to prove 'result' not null below
                destination: ref result._firstChar,
                source: ref Unsafe.Add(ref _firstChar, startIndex));

            return result;
        }

        // Creates a copy of this string in lower case.  The culture is set by culture.
        public string ToLower() => ToLower(null);

        // Creates a copy of this string in lower case.  The culture is set by culture.
        public string ToLower(CultureInfo? culture)
        {
            CultureInfo cult = culture ?? CultureInfo.CurrentCulture;
            return cult.TextInfo.ToLower(this);
        }

        // Creates a copy of this string in lower case based on invariant culture.
        public string ToLowerInvariant()
        {
            return TextInfo.Invariant.ToLower(this);
        }

        public string ToUpper() => ToUpper(null);

        // Creates a copy of this string in upper case.  The culture is set by culture.
        public string ToUpper(CultureInfo? culture)
        {
            CultureInfo cult = culture ?? CultureInfo.CurrentCulture;
            return cult.TextInfo.ToUpper(this);
        }

        // Creates a copy of this string in upper case based on invariant culture.
        public string ToUpperInvariant()
        {
            return TextInfo.Invariant.ToUpper(this);
        }

        // Trims the whitespace from both ends of the string.  Whitespace is defined by
        // char.IsWhiteSpace.
        //
        public string Trim() => TrimWhiteSpaceHelper(TrimType.Both);

        // Removes a set of characters from the beginning and end of this string.
        public unsafe string Trim(char trimChar) => TrimHelper(&trimChar, 1, TrimType.Both);

        // Removes a set of characters from the beginning and end of this string.
        public unsafe string Trim(params char[]? trimChars)
        {
            if (trimChars == null || trimChars.Length == 0)
            {
                return TrimWhiteSpaceHelper(TrimType.Both);
            }
            fixed (char* pTrimChars = &trimChars[0])
            {
                return TrimHelper(pTrimChars, trimChars.Length, TrimType.Both);
            }
        }

        // Removes a set of characters from the beginning of this string.
        public string TrimStart() => TrimWhiteSpaceHelper(TrimType.Head);

        // Removes a set of characters from the beginning of this string.
        public unsafe string TrimStart(char trimChar) => TrimHelper(&trimChar, 1, TrimType.Head);

        // Removes a set of characters from the beginning of this string.
        public unsafe string TrimStart(params char[]? trimChars)
        {
            if (trimChars == null || trimChars.Length == 0)
            {
                return TrimWhiteSpaceHelper(TrimType.Head);
            }
            fixed (char* pTrimChars = &trimChars[0])
            {
                return TrimHelper(pTrimChars, trimChars.Length, TrimType.Head);
            }
        }

        // Removes a set of characters from the end of this string.
        public string TrimEnd() => TrimWhiteSpaceHelper(TrimType.Tail);

        // Removes a set of characters from the end of this string.
        public unsafe string TrimEnd(char trimChar) => TrimHelper(&trimChar, 1, TrimType.Tail);

        // Removes a set of characters from the end of this string.
        public unsafe string TrimEnd(params char[]? trimChars)
        {
            if (trimChars == null || trimChars.Length == 0)
            {
                return TrimWhiteSpaceHelper(TrimType.Tail);
            }
            fixed (char* pTrimChars = &trimChars[0])
            {
                return TrimHelper(pTrimChars, trimChars.Length, TrimType.Tail);
            }
        }

        private string TrimWhiteSpaceHelper(TrimType trimType)
        {
            // end will point to the first non-trimmed character on the right.
            // start will point to the first non-trimmed character on the left.
            int end = Length - 1;
            int start = 0;

            // Trim specified characters.
            if ((trimType & TrimType.Head) != 0)
            {
                for (start = 0; start < Length; start++)
                {
                    if (!char.IsWhiteSpace(this[start]))
                    {
                        break;
                    }
                }
            }

            if ((trimType & TrimType.Tail) != 0)
            {
                for (end = Length - 1; end >= start; end--)
                {
                    if (!char.IsWhiteSpace(this[end]))
                    {
                        break;
                    }
                }
            }

            return CreateTrimmedString(start, end);
        }

        private unsafe string TrimHelper(char* trimChars, int trimCharsLength, TrimType trimType)
        {
            Debug.Assert(trimChars != null);
            Debug.Assert(trimCharsLength > 0);

            // end will point to the first non-trimmed character on the right.
            // start will point to the first non-trimmed character on the left.
            int end = Length - 1;
            int start = 0;

            // Trim specified characters.
            if ((trimType & TrimType.Head) != 0)
            {
                for (start = 0; start < Length; start++)
                {
                    int i = 0;
                    char ch = this[start];
                    for (i = 0; i < trimCharsLength; i++)
                    {
                        if (trimChars[i] == ch)
                        {
                            break;
                        }
                    }
                    if (i == trimCharsLength)
                    {
                        // The character is not in trimChars, so stop trimming.
                        break;
                    }
                }
            }

            if ((trimType & TrimType.Tail) != 0)
            {
                for (end = Length - 1; end >= start; end--)
                {
                    int i = 0;
                    char ch = this[end];
                    for (i = 0; i < trimCharsLength; i++)
                    {
                        if (trimChars[i] == ch)
                        {
                            break;
                        }
                    }
                    if (i == trimCharsLength)
                    {
                        // The character is not in trimChars, so stop trimming.
                        break;
                    }
                }
            }

            return CreateTrimmedString(start, end);
        }

        private string CreateTrimmedString(int start, int end)
        {
            int len = end - start + 1;
            return
                len == Length ? this :
                len == 0 ? string.Empty :
                InternalSubString(start, len);
        }
    }
}
