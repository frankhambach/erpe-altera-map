// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

// The coast and the lake shores as segments, for asking whether a point lies over land or over
// water. A ray from the point crosses them an odd number of times from land and an even number
// from water, a lake counting as water and an island in a lake as land. Even-odd rather than
// nesting, because it needs nothing but the shore lines, and they are closed once 5a has run.
internal sealed class ShoreIndex
{
    private readonly LineIntersector intersector = new RobustLineIntersector();

    private readonly double maximumX = double.NegativeInfinity;

    private readonly STRtree<(Coordinate Start, Coordinate End)> segments = new STRtree<(Coordinate Start, Coordinate End)>();

    public ShoreIndex(IEnumerable<Arc> arcs)
    {
        foreach (Arc arc in arcs.Where(arc => arc.Kind is ArcKind.Coast or ArcKind.Lake))
        {
            Coordinate[] coordinates = arc.GetFullCoordinates();
            for (int i = 0; i < (coordinates.Length - 1); ++i)
            {
                this.segments.Insert(
                    new Envelope(coordinates[i], coordinates[i + 1]),
                    (coordinates[i], coordinates[i + 1]));
                this.maximumX = Math.Max(this.maximumX, Math.Max(coordinates[i].X, coordinates[i + 1].X));
            }
        }
    }

    // Whether the segment between two points touches the shore anywhere, its ends included.
    public bool IsCrossed(Coordinate start, Coordinate end)
    {
        return this.segments.Query(new Envelope(start, end))
            .Any(
                segment =>
                {
                    this.intersector.ComputeIntersection(start, end, segment.Start, segment.End);
                    return this.intersector.HasIntersection;
                });
    }

    // Whether the segment from a node to a point crosses or touches the shore anywhere but at the
    // node itself, where the shore's own segments end.
    public bool IsCrossedApartFrom(Coordinate node, Coordinate point)
    {
        return this.segments.Query(new Envelope(node, point))
            .Any(
                segment =>
                {
                    if (segment.Start.Equals2D(segment.End))
                    {
                        return false;
                    }

                    this.intersector.ComputeIntersection(node, point, segment.Start, segment.End);
                    return this.intersector.HasIntersection
                        && ((this.intersector.IntersectionNum > 1)
                            || !(segment.Start.Equals2D(node) || segment.End.Equals2D(node)));
                });
    }

    public bool IsOverWater(Coordinate point)
    {
        int crossings = point.X >= this.maximumX
            ? 0
            : this.segments.Query(new Envelope(point.X, this.maximumX, point.Y, point.Y))
                .Count(segment => IsCrossedByRay(segment, point));

        return (crossings % 2) == 0;
    }

    // Whether a ray from the point towards increasing x crosses the segment. Half-open in y, so
    // that a ray through a vertex counts it for one of the two segments meeting there, not both.
    private static bool IsCrossedByRay((Coordinate Start, Coordinate End) segment, Coordinate point)
    {
        (Coordinate start, Coordinate end) = segment;
        if ((start.Y > point.Y) == (end.Y > point.Y))
        {
            return false;
        }

        return (start.X + (((point.Y - start.Y) / (end.Y - start.Y)) * (end.X - start.X))) > point.X;
    }
}
