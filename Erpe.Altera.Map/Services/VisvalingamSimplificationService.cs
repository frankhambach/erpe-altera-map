// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;

using Erpe.Altera.Map.Contracts;

using NetTopologySuite.Geometries;

public sealed class VisvalingamSimplificationService : ISimplificationService
{
    private const int MinPoints = 6;

    public Polygon Simplify(Polygon polygon, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (tolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tolerance), "Tolerance must be greater than zero.");
        }

        // Exterior ring
        Coordinate[] exterior = polygon.ExteriorRing.CoordinateSequence.ToCoordinateArray();
        Coordinate[] simplifiedExterior = SimplifyRing(exterior, tolerance);

        // Interior rings (holes)
        List<LinearRing> holes = new List<LinearRing>();
        for (int i = 0; i < polygon.NumInteriorRings; i++)
        {
            Coordinate[] interior = polygon.GetInteriorRingN(i).CoordinateSequence.ToCoordinateArray();
            Coordinate[] simplifiedInterior = SimplifyRing(interior, tolerance);
            if (simplifiedInterior.Length >= 4)
            {
                // linear ring requires at least 4 coordinates (closed)
                holes.Add(polygon.Factory.CreateLinearRing(simplifiedInterior));
            }
        }

        // If exterior got simplified to less than 4 coords, return original polygon to avoid losing it.
        if (simplifiedExterior.Length < 4)
        {
            return polygon;
        }

        return polygon.Factory.CreatePolygon(polygon.Factory.CreateLinearRing(simplifiedExterior), holes.ToArray());
    }

    private static Coordinate[] SimplifyRing(Coordinate[] ringCoordinates, double tolerance)
    {
        if (ringCoordinates.Length == 0)
        {
            return ringCoordinates;
        }

        // Remove the duplicate last coordinate for processing (closed ring)
        bool isClosed = ringCoordinates.Length >= 2 && ringCoordinates[0].Equals2D(ringCoordinates[^1]);
        List<Coordinate> coordinates = new List<Coordinate>(ringCoordinates);
        if (isClosed)
        {
            coordinates.RemoveAt(coordinates.Count - 1);
        }

        int count = coordinates.Count;
        if (count <= Math.Max(3, MinPoints))
        {
            // Re-add closure and return original
            return [..coordinates, new Coordinate(coordinates[0])];
        }

        // Linked-list style arrays
        int[] previousIndexes = new int[count];
        int[] nextIndexes = new int[count];
        bool[] removed = new bool[count];
        double[] area = new double[count];

        for (int index = 0; index < count; index++)
        {
            previousIndexes[index] = (index - 1 + count) % count;
            nextIndexes[index] = (index + 1) % count;
            removed[index] = false;
        }

        for (int index = 0; index < count; index++)
        {
            area[index] = TriangleArea(coordinates[previousIndexes[index]], coordinates[index], coordinates[nextIndexes[index]]);
        }

        // Use a simple priority queue based on SortedSet to pick smallest area
        SortedSet<(double Area, int Index)> queue = new SortedSet<(double, int)>(
            Comparer<(double Area, int Index)>.Create((a, b) =>
            {
                int result = a.Area.CompareTo(b.Area);
                return result != 0 ? result : a.Index.CompareTo(b.Index);
            }));

        for (int index = 0; index < count; index++)
        {
            queue.Add((area[index], index));
        }

        int remaining = count;
        while (queue.Count > 0 && remaining > Math.Max(3, MinPoints))
        {
            (double Area, int Index) smallest = queue.Min;
            if (smallest.Area >= tolerance)
            {
                break;
            }

            queue.Remove(smallest);
            int index = smallest.Index;
            if (removed[index])
            {
                continue;
            }

            // remove idx
            removed[index] = true;
            remaining--;

            int previousIndex = previousIndexes[index];
            int nextIndex = nextIndexes[index];
            nextIndexes[previousIndex] = nextIndex;
            previousIndexes[nextIndex] = previousIndex;

            // update areas for p and nx (if not removed)
            if (!removed[previousIndex])
            {
                queue.Remove((area[previousIndex], previousIndex));
                area[previousIndex] = TriangleArea(coordinates[previousIndexes[previousIndex]], coordinates[previousIndex], coordinates[nextIndexes[previousIndex]]);
                queue.Add((area[previousIndex], previousIndex));
            }

            if (!removed[nextIndex])
            {
                queue.Remove((area[nextIndex], nextIndex));
                area[nextIndex] = TriangleArea(coordinates[previousIndexes[nextIndex]], coordinates[nextIndex], coordinates[nextIndexes[nextIndex]]);
                queue.Add((area[nextIndex], nextIndex));
            }
        }

        // Collect remaining coordinates in order starting from 0 following next links until cycle completes
        List<Coordinate> result = new List<Coordinate>();
        int start = -1;
        for (int index = 0; index < count; index++)
        {
            if (!removed[index])
            {
                start = index;
                break;
            }
        }

        if (start == -1)
        {
            // all removed? fallback to original with closure
            return [..coordinates, new Coordinate(coordinates[0])];
        }

        int current = start;
        do
        {
            result.Add(new Coordinate(coordinates[current]));
            current = nextIndexes[current];
        }
        while (current != start);

        // Re-close ring
        result.Add(new Coordinate(result[0]));
        return result.ToArray();
    }

    private static double TriangleArea(Coordinate a, Coordinate b, Coordinate c)
    {
        // planar triangle area (absolute)
        double area = Math.Abs(((a.X * (b.Y - c.Y)) + (b.X * (c.Y - a.Y)) + (c.X * (a.Y - b.Y))) / 2.0);
        return area;
    }
}