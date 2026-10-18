// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

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

    public IReadOnlyCollection<(double X, double Y)> SelectRemoved(
        IReadOnlyCollection<(Coordinate[] Line, int MinimumCount)> lines,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(lines);

        Dictionary<(double X, double Y), int> carried = [];
        Dictionary<(double X, double Y), int> offered = [];
        foreach ((Coordinate[] line, int minimumCount) in lines)
        {
            Count(carried, line);
            Count(offered, this.SelectRemoved(line, tolerance, minimumCount));
        }

        // Equality rather than a comparison, because a coordinate offered more often than it is
        // carried would mean this had miscounted, and keeping it is the safe way to be wrong.
        return [.. offered.Where(pair => pair.Value == carried[pair.Key]).Select(pair => pair.Key)];
    }

    public (IReadOnlySet<(double X, double Y)> Removed, int Raised, int Rounds) SelectRemoved(
        IReadOnlyList<(Coordinate[] Line, int MinimumCount)> lines,
        IReadOnlyList<int[]> rings,
        Func<int, IReadOnlySet<(double X, double Y)>, bool> survives,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(rings);
        ArgumentNullException.ThrowIfNull(survives);

        // Through the same floor the single-line overload applies, so that a count of nothing
        // cannot be doubled to nothing for ever.
        int[] counts = [.. lines.Select(line => Math.Max(2, line.MinimumCount))];
        int raised = 0;
        int rounds = 0;
        while (true)
        {
            HashSet<(double X, double Y)> removed =
            [
                .. this.SelectRemoved(
                    [.. lines.Select((line, index) => (line.Line, counts[index]))],
                    tolerance),
            ];
            int[] wanting =
            [
                .. Enumerable.Range(0, rings.Count)
                    .Where(ring => !survives(ring, removed))
                    .SelectMany(ring => rings[ring])
                    .Distinct()
                    .Where(line => counts[line] < lines[line].Line.Length),
            ];
            if (wanting.Length == 0)
            {
                return (removed, raised, rounds);
            }

            foreach (int line in wanting)
            {
                counts[line] = Math.Min(lines[line].Line.Length, counts[line] * 2);
            }

            raised += wanting.Length;
            ++rounds;
        }
    }

    // Each coordinate once per line, however many times that line carries it, so that a line
    // running over the same point twice cannot outvote itself.
    private static void Count(Dictionary<(double X, double Y), int> counts, IEnumerable<Coordinate> coordinates)
    {
        foreach ((double X, double Y) point in coordinates
                     .Select(coordinate => (coordinate.X, coordinate.Y))
                     .ToHashSet())
        {
            counts[point] = counts.GetValueOrDefault(point) + 1;
        }
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
