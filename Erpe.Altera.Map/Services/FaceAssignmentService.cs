// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;

using Serilog;

public sealed class FaceAssignmentService : IFaceAssignmentService
{
    // Two countries sharing a face only matters when there is a face worth sharing. Their
    // shapes overlap slightly all along every border they do share, which leaves slivers of
    // all but no area in both at once; only a face of real size means a tracer is missing.
    private const double FusedFaceArea = 25.0;

    private readonly GeometryFactory geometryFactory;

    public FaceAssignmentService(GeometryFactory geometryFactory)
    {
        ArgumentNullException.ThrowIfNull(geometryFactory);

        this.geometryFactory = geometryFactory;
    }

    public void Assign(ArcGraph arcGraph, IEnumerable<Country> countries)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);
        ArgumentNullException.ThrowIfNull(countries);

        bool[] isWater = GetWater(arcGraph);
        STRtree<(Polygon Polygon, Country Country)> shapes = BuildShapeIndex(countries);

        int water = 0;
        int assigned = 0;
        int fused = 0;
        List<Face> unclaimed = [];
        for (int i = 0; i < arcGraph.Faces.Count; ++i)
        {
            Face face = arcGraph.Faces[i];
            if (isWater[i])
            {
                // Lakes are water, not territory. Nothing claims them, so they are simply
                // left out of the countries rather than being split between them.
                ++water;
                continue;
            }

            Polygon polygon = this.CreatePolygon(face.Shell);
            if (polygon.IsEmpty)
            {
                continue;
            }

            // Grouped by country, not by shape. A country is made of many shapes and they
            // routinely overlap each other, so counting shapes would report almost every
            // face as belonging to two countries at once.
            Point point = polygon.InteriorPoint;
            IGrouping<Country, (Polygon Polygon, Country Country)>[] containing =
                shapes.Query(point.EnvelopeInternal)
                    .Where(shape => shape.Polygon.Contains(point))
                    .GroupBy(shape => shape.Country)
                    .ToArray();

            if (containing.Length == 0)
            {
                unclaimed.Add(face);
                continue;
            }

            // More than one country containing the same face means their shapes overlap
            // there. The larger share wins, but a large face in that position is the sign
            // of a missing tracer, which no rule here can repair.
            face.Owner = containing.Length == 1
                ? containing[0].Key
                : containing.MaxBy(
                    grouping => grouping.Sum(shape => GetOverlap(shape.Polygon, polygon)))!.Key;
            ++assigned;

            // Two countries covering the same face means the border between them was never
            // drawn and they have fused. No rule here can repair that; the tracer has to be
            // added to the source map, so each one is reported individually.
            if (containing.Length > 1)
            {
                ++fused;
                if (polygon.Area < FusedFaceArea)
                {
                    continue;
                }

                Log.Error(
                    "Face {FaceId} of {Area:0.0} square units at {X:0.##}, {Y:0.##} lies inside {Count} countries "
                    + "at once, including {First} and {Second}. The tracer between them is missing.",
                    face.Id,
                    polygon.Area,
                    point.X,
                    point.Y,
                    containing.Length,
                    containing[0].Key.Id,
                    containing[1].Key.Id);
            }
        }

        int absorbed = Absorb(arcGraph, unclaimed);

        Log.Information(
            "Assigned {Assigned} faces to countries, left {Water} as water and absorbed {Absorbed} of "
            + "{Unclaimed} unclaimed faces into their neighbours. {Fused} faces lie in two countries at once, "
            + "all but the reported ones too small to matter.",
            assigned,
            water,
            absorbed,
            unclaimed.Count,
            fused);
    }

    // A face the lake shore alone bounds is water. Anything with a border or a sea coast in
    // it is territory, however it happens to nest.
    //
    // Nesting cannot decide this. A lake with a border ending on its shore is not enclosed at
    // all in the topological sense: the land faces around it take the lake shore into their
    // own boundaries, so no face carries the lake as a hole and there is nothing to be inside
    // of. That is the case for 126 of the 462 lakes here, including every large one. Nesting
    // also mistakes an enclave, which is land enclosed by land, for water.
    private static bool[] GetWater(ArcGraph arcGraph)
    {
        return
        [
            .. arcGraph.Faces.Select(
                face => !face.Shell.IsEmpty && face.Shell.All(directed => directed.Arc.Kind == ArcKind.Lake)),
        ];
    }

    private static STRtree<(Polygon Polygon, Country Country)> BuildShapeIndex(IEnumerable<Country> countries)
    {
        STRtree<(Polygon Polygon, Country Country)> index = new STRtree<(Polygon Polygon, Country Country)>();
        foreach ((Polygon polygon, Country country) in countries.SelectMany(
                     country => country.Shapes.Where(shape => !shape.Polygon.IsEmpty)
                         .Select(shape => (shape.Polygon, Country: country))))
        {
            index.Insert(polygon.EnvelopeInternal, (polygon, country));
        }

        return index;
    }

    private static double GetOverlap(Geometry shape, Geometry face)
    {
        try
        {
            return shape.Intersection(face).Area;
        }
        catch (TopologyException)
        {
            // A shape that self-intersects cannot be overlaid. Falling back on the envelope
            // keeps the larger share winning without the whole run failing for one bad ring.
            return shape.EnvelopeInternal.Intersection(face.EnvelopeInternal).Area;
        }
    }

    // A face no country claims is a gap: unclaimed ground between countries that the border
    // network enclosed but no shape covers. It goes to the neighbour it shares the most
    // border with, which is the whole of gap filling in this representation.
    private static int Absorb(ArcGraph arcGraph, List<Face> unclaimed)
    {
        Dictionary<DirectedArc, Face> owners = new Dictionary<DirectedArc, Face>();
        foreach (Face face in arcGraph.Faces)
        {
            foreach (DirectedArc directed in face.Shell)
            {
                owners[directed] = face;
            }
        }

        // Repeated until nothing moves, because a run of unclaimed faces can be several deep:
        // the innermost only gains an owned neighbour once the one outside it has been
        // absorbed, so a single pass leaves whatever it happened to reach first.
        int absorbed = 0;
        List<Face> remaining = [.. unclaimed];
        while (remaining.Count > 0)
        {
            List<Face> next = [];
            foreach (Face face in remaining)
            {
                Country? owner = face.Shell
                    .Select(directed => new DirectedArc(directed.Arc, !directed.IsReversed))
                    .Where(owners.ContainsKey)
                    .GroupBy(directed => owners[directed].Owner)
                    .Where(grouping => grouping.Key != null)
                    .OrderByDescending(grouping => grouping.Sum(directed => GetLength(directed)))
                    .Select(grouping => grouping.Key)
                    .FirstOrDefault();
                if (owner == null)
                {
                    next.Add(face);
                    continue;
                }

                face.Owner = owner;
                ++absorbed;
            }

            if (next.Count == remaining.Count)
            {
                remaining = next;
                break;
            }

            remaining = next;
        }

        // A face left here has no owned neighbour at all, which means nothing around it is
        // territory either. It is unclaimed land in open water, and there is no neighbour to
        // give it to.
        foreach (Face face in remaining)
        {
            Log.Warning(
                "Face {FaceId} is claimed by no country and borders none, so nothing can absorb it.",
                face.Id);
        }

        return absorbed;
    }

    private static double GetLength(DirectedArc directed)
    {
        Coordinate[] coordinates = directed.Arc.GetFullCoordinates();
        double length = 0.0;
        for (int i = 1; i < coordinates.Length; ++i)
        {
            length += coordinates[i].Distance(coordinates[i - 1]);
        }

        return length;
    }

    private Polygon CreatePolygon(ImmutableArray<DirectedArc> ring)
    {
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

        if (coordinates.Count < 3)
        {
            return Polygon.Empty;
        }

        if (!coordinates[0].Equals2D(coordinates[^1]))
        {
            coordinates.Add(coordinates[0]);
        }

        return this.geometryFactory.CreatePolygon(coordinates.ToArray());
    }
}
