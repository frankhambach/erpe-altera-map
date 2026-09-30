// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

public static class EnumerableExtensions
{
    public static IEnumerable<IEnumerable<T>> Chunk<T>(this IEnumerable<T> items, int size)
    {
        ArgumentNullException.ThrowIfNull(items);

        return ChunkInternal(items, size);
    }

    public static IEnumerable<IList<TSource>> ChunkBy<TSource>(
        this IEnumerable<TSource> source,
        Func<TSource, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);

        return ChunkByInternal(source, predicate);
    }

    private static IEnumerable<IEnumerable<T>> ChunkInternal<T>(this IEnumerable<T> items, int size)
    {
        using IEnumerator<T> enumerator = items.GetEnumerator();
        bool hasNext = enumerator.MoveNext();

        IEnumerable<T> NextPartitionOf()
        {
            int remainingCountForPartition = size;
            while ((remainingCountForPartition-- > 0) && hasNext)
            {
                yield return enumerator.Current;
                hasNext = enumerator.MoveNext();
            }
        }

        while (hasNext)
        {
            yield return NextPartitionOf().ToArray();
        }
    }

    private static IEnumerable<IList<TSource>> ChunkByInternal<TSource>(
        this IEnumerable<TSource> source,
        Func<TSource, bool> predicate)
    {
        List<TSource> chunk = new List<TSource>();

        foreach (TSource item in source)
        {
            if (predicate(item))
            {
                if (chunk.Count > 0)
                {
                    yield return chunk;
                }

                chunk = new List<TSource>();
            }

            chunk.Add(item);
        }

        yield return chunk;
    }
}