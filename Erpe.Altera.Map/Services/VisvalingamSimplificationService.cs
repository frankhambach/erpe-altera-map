// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;

using Erpe.Altera.Map.Contracts;

using NetTopologySuite.Geometries;

// Visvalingam-Whyatt, over one open line at a time: take the vertex whose triangle with
// its two neighbours is smallest, drop it, recompute the two neighbours, and carry on
// until the smallest triangle left is bigger than the tolerance.
//
// Open, not cyclic, because what is simplified here is an arc rather than a ring. The two
// ends of an arc are nodes, where it meets the arcs around it, and a node that moved or
// vanished would pull the network apart.
public sealed class VisvalingamSimplificationService : ISimplificationService
{
    public IEnumerable<Coordinate> SelectRemoved(Coordinate[] line, double tolerance, int minimumCount)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (tolerance <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be greater than zero.");
        }

        int floor = Math.Max(2, minimumCount);
        if (line.Length <= floor)
        {
            return [];
        }

        return SelectRemovedInternal(line, tolerance, floor);
    }

    private static List<Coordinate> SelectRemovedInternal(Coordinate[] line, double tolerance, int floor)
    {
        int count = line.Length;
        int[] previous = new int[count];
        int[] next = new int[count];
        bool[] removed = new bool[count];
        double[] areas = new double[count];
        for (int i = 0; i < count; ++i)
        {
            previous[i] = i - 1;
            next[i] = i + 1;
        }

        // Only the interior ever enters the queue, which is how the two ends stay put.
        SortedSet<(double Area, int Index)> queue = new SortedSet<(double Area, int Index)>(
            Comparer<(double Area, int Index)>.Create(
                (first, second) => first.Area.CompareTo(second.Area) != 0
                    ? first.Area.CompareTo(second.Area)
                    : first.Index.CompareTo(second.Index)));
        for (int i = 1; i < (count - 1); ++i)
        {
            areas[i] = GetArea(line[i - 1], line[i], line[i + 1]);
            queue.Add((areas[i], i));
        }

        int remaining = count;
        while ((queue.Count > 0) && (remaining > floor))
        {
            (double Area, int Index) smallest = queue.Min;
            if (smallest.Area >= tolerance)
            {
                break;
            }

            queue.Remove(smallest);
            int index = smallest.Index;
            removed[index] = true;
            --remaining;

            int before = previous[index];
            int after = next[index];
            next[before] = after;
            previous[after] = before;
            Requeue(queue, line, previous, next, areas, removed, before, count);
            Requeue(queue, line, previous, next, areas, removed, after, count);
        }

        List<Coordinate> dropped = new List<Coordinate>();
        for (int i = 1; i < (count - 1); ++i)
        {
            if (removed[i])
            {
                dropped.Add(line[i]);
            }
        }

        return dropped;
    }

    private static void Requeue(
        SortedSet<(double Area, int Index)> queue,
        Coordinate[] line,
        int[] previous,
        int[] next,
        double[] areas,
        bool[] removed,
        int index,
        int count)
    {
        if ((index <= 0) || (index >= (count - 1)) || removed[index])
        {
            return;
        }

        queue.Remove((areas[index], index));
        areas[index] = GetArea(line[previous[index]], line[index], line[next[index]]);
        queue.Add((areas[index], index));
    }

    private static double GetArea(Coordinate first, Coordinate second, Coordinate third)
    {
        return Math.Abs(
                (first.X * (second.Y - third.Y))
                + (second.X * (third.Y - first.Y))
                + (third.X * (first.Y - second.Y)))
            / 2.0;
    }
}
