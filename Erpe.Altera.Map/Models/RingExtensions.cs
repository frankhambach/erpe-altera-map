// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System;
using System.Collections.Generic;

using NetTopologySuite.Geometries;

internal static class RingExtensions
{
    // Below this a ring has no winding worth having. One unit of output precision squared: a ring
    // enclosing less than that encloses less than the smallest area six decimals can express, so
    // which way round it runs says nothing and turning it changes no coordinate.
    private const double DegenerateArea = 1E-12;

    // Which way a closed run of coordinates is wound, in the coordinates it is handed: 1
    // counter-clockwise, -1 clockwise, and 0 for one that encloses nothing. Both writers go
    // through this so that neither can disagree with the other about a ring.
    public static int GetWinding(this IReadOnlyList<Coordinate> coordinates)
    {
        double area = coordinates.GetSignedArea();
        if (Math.Abs(area) < DegenerateArea)
        {
            return 0;
        }

        return area > 0.0 ? 1 : -1;
    }

    // Twice the area a closed run of coordinates encloses, halved: positive counter-clockwise and
    // negative clockwise, in whatever coordinates it is handed. Which way round that looks on a
    // screen depends on which way y points, so a caller comparing it against a convention has to
    // do that in the coordinates the convention is about - the source map has y running down the
    // page and the output has it running up, and every ring turns over in between.
    //
    // A run that does not come back to where it started is closed by this rather than by the
    // caller, since the shoelace is only an area at all for a closed one.
    public static double GetSignedArea(this IReadOnlyList<Coordinate> coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);

        double area = 0.0;
        for (int i = 0; i < coordinates.Count; ++i)
        {
            Coordinate current = coordinates[i];
            Coordinate next = coordinates[(i + 1) % coordinates.Count];
            area += (current.X * next.Y) - (next.X * current.Y);
        }

        return area / 2.0;
    }

    // The arcs of a ring walked end to end as one run of coordinates. Each arc's first
    // coordinate repeats the previous arc's last one, being the node between them, and the
    // repeat is dropped.
    //
    // The run is left open: it does not come back to its first coordinate even though the
    // ring does. That is what lets a caller tell a boundary that returns to a node it has
    // already been through from one that merely ends where it began, which is the
    // difference between a face that has to be cut into lobes and one that does not.
    public static List<Coordinate> GetCoordinates(this IEnumerable<DirectedArc> ring)
    {
        ArgumentNullException.ThrowIfNull(ring);

        List<Coordinate> coordinates = [];
        foreach (DirectedArc directed in ring)
        {
            Coordinate[] arcCoordinates = directed.Arc.GetFullCoordinates();
            if (directed.IsReversed)
            {
                Array.Reverse(arcCoordinates);
            }

            foreach (Coordinate coordinate in arcCoordinates)
            {
                if ((coordinates.Count == 0) || !coordinates[^1].Equals2D(coordinate))
                {
                    coordinates.Add(coordinate);
                }
            }
        }

        if ((coordinates.Count > 1) && coordinates[0].Equals2D(coordinates[^1]))
        {
            coordinates.RemoveAt(coordinates.Count - 1);
        }

        return coordinates;
    }
}
