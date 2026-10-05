// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Models;

using System;
using System.Collections.Generic;

using NetTopologySuite.Geometries;

internal static class RingExtensions
{
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
