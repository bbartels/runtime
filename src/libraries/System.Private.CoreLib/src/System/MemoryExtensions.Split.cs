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

        private enum SpanSplitEnumeratorMode
        {
            None = 0,
            SingleToken,
            Sequence,
            EmptySequence,
            Any,
            SearchValues
        }

        public ref struct SpanSplitEnumerator<T> where T : IEquatable<T>
        {
            private readonly ReadOnlySpan<T> _span;

            private readonly T _separator = default!;
            private readonly ReadOnlySpan<T> _separatorBuffer;
            private readonly SearchValues<T> _searchValues = default!;

            private readonly int _separatorLength;
            private readonly SpanSplitEnumeratorMode _splitMode;

            private int _startCurrent = 0;
            private int _endCurrent = 0;
            private int _startNext = 0;

            public SpanSplitEnumerator<T> GetEnumerator() => this;

            public Range Current => new Range(_startCurrent, _endCurrent);

            internal SpanSplitEnumerator(ReadOnlySpan<T> span, SearchValues<T> searchValues)
            {
                _span = span;
                _splitMode = SpanSplitEnumeratorMode.SearchValues;
                _searchValues = searchValues;
            }

            internal SpanSplitEnumerator(ReadOnlySpan<T> span, ReadOnlySpan<T> separator, bool treatAsSingleSeparator)
            {
                _span = span;
                _separatorBuffer = separator;
                _splitMode = (separator.Length, treatAsSingleSeparator) switch
                {
                    (0, true) => SpanSplitEnumeratorMode.EmptySequence,
                    (_, true) => SpanSplitEnumeratorMode.Sequence,
                    _ => SpanSplitEnumeratorMode.Any
                };
            }

            internal SpanSplitEnumerator(ReadOnlySpan<T> span, T separator)
            {
                _span = span;
                _separator = separator;
                _splitMode = SpanSplitEnumeratorMode.SingleToken;
            }

            public bool MoveNext()
            {
                if (_splitMode is SpanSplitEnumeratorMode.None || _startNext > _span.Length)
                {
                    return false;
                }

                ReadOnlySpan<T> slice = _span[_startNext..];

                (int separatorIndex, int separatorLength) = _splitMode switch
                {
                    SpanSplitEnumeratorMode.SingleToken   => (slice.IndexOf(_separator), 1),
                    SpanSplitEnumeratorMode.Sequence      => (slice.IndexOf(_separatorBuffer), _separatorBuffer.Length),
                    SpanSplitEnumeratorMode.EmptySequence => (slice.IndexOf(_separatorBuffer), 1),
                    SpanSplitEnumeratorMode.Any           => (slice.IndexOfAny(_separatorBuffer), 1),
                    SpanSplitEnumeratorMode.SearchValues  => (_searchValues.IndexOfAny(_span), 1),
                    _ => throw new UnreachableException()
                };

                int elementLength = (separatorIndex != -1 ? separatorIndex : slice.Length);

                _startCurrent = _startNext;
                _endCurrent = _startCurrent + elementLength;
                _startNext = _endCurrent + separatorLength;
                return true;
            }
        }
    }
}
