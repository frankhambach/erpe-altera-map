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
using NetTopologySuite.Operation.OverlayNG;

using Serilog;

public sealed class FaceAssignmentService : IFaceAssignmentService
{
    // Two countries sharing a face only matters when there is a face worth sharing. Their
    // shapes overlap slightly all along every border they do share, which leaves slivers of
    // all but no area in both at once; only a face of real size means a tracer is missing.
    private const double FusedFaceArea = 25.0;

    // How much of a face a country has to cover before it counts as claiming it. Shapes
    // overlap their neighbours slightly all along every border they share, so without this
    // every face would be claimed by everything around it.
    //
    // Measured over the 10,397 claims the 6586 land faces attract: 3776 cover less than a
    // twentieth of their face, 4 cover between a twentieth and a tenth, and then nothing at
    // all covers between a tenth and a fifth, after which 40 claims spread from 0.2 to 0.9
    // and 6577 cover nine tenths or more. Any threshold inside that empty tenth classifies
    // the sample identically - at 0.10 and at 0.20 the same 33 faces come out with two
    // claimants - which is what makes this a reading of the data rather than a setting.
    private const double MinimumClaimShare = 0.15;

    private readonly GeometryFactory geometryFactory;

    public FaceAssignmentService(GeometryFactory geometryFactory)
    {
        ArgumentNullException.ThrowIfNull(geometryFactory);

        this.geometryFactory = geometryFactory;
    }

    public void Assign(ArcGraph arcGraph, IEnumerable<Country> countries, bool absorbUnclaimed)
    {
        ArgumentNullException.ThrowIfNull(arcGraph);
        ArgumentNullException.ThrowIfNull(countries);

        bool[] isWater = GetWater(arcGraph);
        STRtree<(Polygon Polygon, Country Country)> shapes = BuildShapeIndex(countries);

        int water = 0;
        double waterArea = 0.0;
        int assigned = 0;
        int fused = 0;
        int reported = 0;
        List<Face> unclaimed = [];
        for (int i = 0; i < arcGraph.Faces.Count; ++i)
        {
            Face face = arcGraph.Faces[i];
            if (isWater[i])
            {
                waterArea += Math.Abs(this.CreatePolygon(face.Shell).Area);

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

            // How much of the face each country covers, rather than which shapes contain
            // one point of it. A point can only report a face as shared where the two
            // shapes overlap each other, and on a fused pair that is a sliver along some
            // other border they really do share rather than the face itself - so the point
            // test misses the fusion in almost every case where it matters, and hands the
            // whole face to whichever country the point happened to fall in.
            //
            // Grouped by country, not by shape: a country is made of many shapes, and they
            // routinely overlap each other.
            double area = Math.Abs(polygon.Area);
            Claim[] claims =
            [
                .. shapes.Query(polygon.EnvelopeInternal)
                    .GroupBy(shape => shape.Country)
                    .Select(
                        grouping => new Claim(
                            grouping.Key,
                            this.GetClaimedArea(grouping.Select(shape => shape.Polygon), polygon, area)))
                    .Where(claim => (claim.Area > 0.0) && (claim.Area >= area * MinimumClaimShare))
                    .OrderByDescending(claim => claim.Area),
            ];

            if (claims.Length == 0)
            {
                unclaimed.Add(face);
                continue;
            }

            // The largest share wins. Where two countries each cover a real part of one
            // face there is no better answer available: the border between them was never
            // drawn, so nothing here knows where it would have run.
            face.Owner = claims[0].Country;
            ++assigned;

            // Two countries covering the same face means the border between them is missing
            // and they have fused. The tracer has to be added to the source map, so each
            // one large enough to matter is reported individually.
            if (claims.Length > 1)
            {
                ++fused;
                if (area < FusedFaceArea)
                {
                    continue;
                }

                ++reported;
                Point point = polygon.InteriorPoint;
                Log.Error(
                    "Face {FaceId} of {Area:0.0} square units at {X:0.##}, {Y:0.##} is covered by {Count} "
                    + "countries at once, {First} by {FirstShare:0.00} of it and {Second} by {SecondShare:0.00}. "
                    + "The tracer between them is missing.",
                    face.Id,
                    area,
                    point.X,
                    point.Y,
                    claims.Length,
                    claims[0].Country.Id,
                    claims[0].Area / area,
                    claims[1].Country.Id,
                    claims[1].Area / area);
            }
        }

        // Only where the countries are meant to cover everything between them. Substates
        // cover a few countries and nothing else, so absorbing there would pull whole
        // foreign countries into whichever substate happened to border them.
        int absorbed = absorbUnclaimed ? Absorb(arcGraph, unclaimed) : 0;

        Log.Information(
            "Assigned {Assigned} faces to countries, left {Water} as water covering {WaterArea:0.0} square "
            + "units and absorbed {Absorbed} of "
            + "{Unclaimed} unclaimed faces into their neighbours. {Fused} faces are covered by more than one "
            + "country at once, {Reported} of them large enough to matter and reported above.",
            assigned,
            water,
            waterArea,
            absorbed,
            unclaimed.Count,
            fused,
            reported);
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

    private static Geometry GetOverlapGeometry(Geometry shape, Polygon face)
    {
        try
        {
            return shape.Intersection(face);
        }
        catch (TopologyException)
        {
            // A shape that self-intersects cannot be overlaid. Falling back on the envelope
            // keeps the larger share winning without the whole run failing for one bad ring.
            return face.Factory.ToGeometry(shape.EnvelopeInternal.Intersection(face.EnvelopeInternal));
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

    // A country can have several shapes over one face and they routinely overlap each
    // other, so their parts are unioned rather than added up. The result is capped at the
    // face, because the fallback in GetOverlapGeometry measures an envelope when a shape is
    // too broken to overlay and an envelope can be larger than the face itself; 42 of the
    // claims on this map are measured that way.
    private double GetClaimedArea(IEnumerable<Polygon> candidates, Polygon face, double faceArea)
    {
        Geometry[] parts =
        [
            .. candidates.Select(shape => GetOverlapGeometry(shape, face)).Where(part => !part.IsEmpty),
        ];

        double claimed = parts.Length switch
            {
                0 => 0.0,
                1 => parts[0].Area,
                _ => UnaryUnionNG.Union(parts, this.geometryFactory, this.geometryFactory.PrecisionModel).Area,
            };

        return Math.Min(claimed, faceArea);
    }

    private Polygon CreatePolygon(ImmutableArray<DirectedArc> ring)
    {
        List<Coordinate> coordinates = ring.GetCoordinates();
        if (coordinates.Count < 3)
        {
            return Polygon.Empty;
        }

        coordinates.Add(coordinates[0]);

        return this.geometryFactory.CreatePolygon(coordinates.ToArray());
    }

    // What one country covers of one face.
    private readonly record struct Claim(Country Country, double Area);
}
