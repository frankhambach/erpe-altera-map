// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Erpe.Altera.Map.Models;

using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

// Where arcs that ought to meet only at their nodes meet anywhere else. A graph of arcs is a
// planar partition only if they do not, and faces traced from one where they do come out wrong
// without a word, so this is measured rather than assumed wherever arcs are put together that
// were not noded against each other.
internal static class ArcCrossings
{
    // Every point at which a segment of the arcs asked about meets a segment of those or of the
    // others anywhere but at an end the two share, with the first few named. A segment of no length
    // crosses nothing: the coast carries a few, two equal points in a row at the end of an arc, and
    // the intersector took each for an overlap.
    public static (int Count, IReadOnlyList<string> Where) Count(
        IEnumerable<Arc> asked,
        IEnumerable<Arc> others,
        int reported)
    {
        STRtree<(Arc Arc, int Index, Coordinate Start, Coordinate End)> segments =
            new STRtree<(Arc Arc, int Index, Coordinate Start, Coordinate End)>();
        List<Arc> askedArcs = [.. asked];
        HashSet<Arc> askedSet = [.. askedArcs];
        foreach (Arc arc in askedArcs.Concat(others.Where(arc => !askedSet.Contains(arc))))
        {
            Coordinate[] coordinates = arc.GetFullCoordinates();
            for (int i = 0; i < (coordinates.Length - 1); ++i)
            {
                segments.Insert(new Envelope(coordinates[i], coordinates[i + 1]), (arc, i, coordinates[i], coordinates[i + 1]));
            }
        }

        LineIntersector intersector = new RobustLineIntersector();
        List<string> where = [];
        int count = 0;
        foreach (Arc arc in askedArcs)
        {
            Coordinate[] coordinates = arc.GetFullCoordinates();
            for (int i = 0; i < (coordinates.Length - 1); ++i)
            {
                Coordinate start = coordinates[i];
                Coordinate end = coordinates[i + 1];
                foreach ((Arc otherArc, int otherIndex, Coordinate otherStart, Coordinate otherEnd) in segments.Query(
                             new Envelope(start, end)))
                {
                    // Each pair of asked segments once, and never a segment against itself.
                    bool isSeen = askedSet.Contains(otherArc) && ((otherArc.Id, otherIndex).CompareTo((arc.Id, i)) <= 0);
                    if (isSeen || otherStart.Equals2D(otherEnd) || start.Equals2D(end))
                    {
                        continue;
                    }

                    intersector.ComputeIntersection(start, end, otherStart, otherEnd);
                    if (!intersector.HasIntersection
                        || ((intersector.IntersectionNum == 1) && IsSharedEnd(start, end, otherStart, otherEnd)))
                    {
                        continue;
                    }

                    ++count;
                    if (where.Count < reported)
                    {
                        Coordinate point = intersector.GetIntersection(0);
                        where.Add(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"{arc.Kind} {arc.Id} and {otherArc.Kind} {otherArc.Id} at {point.X:F4}, {point.Y:F4}"));
                    }
                }
            }
        }

        return (count, where);
    }

    // Whether two segments that meet at a single point share an end, which is then the point: two
    // straight segments meet once at most, and an end of both is a point of both. Asked of the
    // ends rather than of the point the intersector computes, which for a segment of 0.0001 units
    // ending at a node came out a rounding away from the node itself.
    private static bool IsSharedEnd(Coordinate start, Coordinate end, Coordinate otherStart, Coordinate otherEnd)
    {
        return start.Equals2D(otherStart) || start.Equals2D(otherEnd) || end.Equals2D(otherStart) || end.Equals2D(otherEnd);
    }
}
