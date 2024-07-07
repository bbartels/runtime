// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
using System.Buffers;
using System.Diagnostics;

namespace System
{
    public static partial class MemoryExtensions
    {
        public static SpanSplitEnumerator<T> Split<T>(this ReadOnlySpan<T> source, T separator)
           where T : IEquatable<T> => new SpanSplitEnumerator<T>(source, separator);
        public static SpanSplitEnumerator<T> Split<T>(this ReadOnlySpan<T> source, ReadOnlySpan<T> separator)
            where T : IEquatable<T> => new SpanSplitEnumerator<T>(source, separator, treatAsSingleSeparator: true);
        public static SpanSplitEnumerator<T> SplitAny<T>(this ReadOnlySpan<T> source, [System.Diagnostics.CodeAnalysis.UnscopedRef] params ReadOnlySpan<T> separators)
            where T : IEquatable<T> => new SpanSplitEnumerator<T>(source, separators, treatAsSingleSeparator: false);
        public static SpanSplitEnumerator<T> SplitAny<T>(this ReadOnlySpan<T> source, SearchValues<T> separators)
            where T : IEquatable<T> => new SpanSplitEnumerator<T>(source, separators);

        public ref struct SpanSplitEnumerator<T> where T : IEquatable<T>
        {
            private enum SplitMode
            {
                None = 0,
                SingleToken,
                Sequence,
                Any,
                SearchValues
            }

            private readonly ReadOnlySpan<T> _span;

            private readonly T _separator = default!;
            private readonly ReadOnlySpan<T> _separatorBuffer;
            private readonly SearchValues<T> _searchValues = default!;

            private readonly int _separatorLength;
            private readonly SplitMode _splitMode;

            private readonly bool _isInitialized = true;

            private int _startCurrent = 0;
            private int _endCurrent = 0;
            private int _startNext = 0;

            public SpanSplitEnumerator<T> GetEnumerator() => this;

            public Range Current => new Range(_startCurrent, _endCurrent);

            internal SpanSplitEnumerator(ReadOnlySpan<T> span, SearchValues<T> searchValues)
            {
                _span = span;
                _separatorLength = 1;
                _splitMode = SplitMode.SearchValues;
                _searchValues = searchValues;
            }

            internal SpanSplitEnumerator(ReadOnlySpan<T> span, ReadOnlySpan<T> separator, bool treatAsSingleSeparator)
            {
                _span = span;
                _separatorBuffer = separator;
                _separatorLength = (_separatorBuffer.Length, treatAsSingleSeparator) switch
                {
                    (0, true) or (_, false) => 1,
                    _ => separator.Length
                };
                _splitMode = treatAsSingleSeparator ? SplitMode.Sequence : SplitMode.Any;
            }

            internal SpanSplitEnumerator(ReadOnlySpan<T> span, T separator)
            {
                _span = span;
                _separator = separator;
                _separatorLength = 1;
                _splitMode = SplitMode.SingleToken;
            }

            public bool MoveNext()
            {
                if (!_isInitialized || _startNext > _span.Length)
                {
                    return false;
                }

                ReadOnlySpan<T> slice = _span[_startNext..];

                int separatorIndex = _splitMode switch
                {
                    SplitMode.SingleToken => slice.IndexOf(_separator),
                    SplitMode.Sequence => slice.IndexOf(_separatorBuffer),
                    SplitMode.Any => slice.IndexOfAny(_separatorBuffer),
                    SplitMode.SearchValues => _searchValues.IndexOfAny(_span),
                    _ => throw new UnreachableException()
                };

                int elementLength = (separatorIndex != -1 ? separatorIndex : slice.Length);

                _startCurrent = _startNext;
                _endCurrent = _startCurrent + elementLength;
                _startNext = _endCurrent + _separatorLength;
                return true;
            }
        }
    }
}
