// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;
using NetTopologySuite.Index.Strtree;
using NetTopologySuite.Operation.OverlayNG;

using Serilog;

public sealed class CoastlineService : ICoastlineService
{
    // A hole in the landmass is either a gap between countries or a real inland sea, and
    // area over perimeter separates them. That ratio is a hydraulic radius: it has units
    // of length and scales linearly with the size of a fixed shape, so it measures both
    // thinness and smallness at once and no separate area test is needed.
    //
    // Measured over the 3094 holes in the sample map, the ratio is sharply bimodal: 2632
    // gaps top out at 0.0955 units and the 462 lakes start again at 0.1965, a span of
    // 2.06x with nothing in it at all. This threshold sits near the middle of that empty
    // valley, so any value between the two bounds below classifies the sample identically.
    private const double MaximumGapAreaToLengthRatio = 0.15;

    // The valley bounds, rounded inwards from the measured extremes. A hole landing inside
    // them is reported, because the separation is what makes the threshold trustworthy
    // rather than a tuned guess: if a future export starts populating the valley, the ratio
    // has stopped being a clean discriminator and the figure needs rethinking, not nudging.
    private const double GapRatioUpperBound = 0.10;

    private const double LakeRatioLowerBound = 0.19;

    private const double DegenerateLength = 1E-9;

    // Ten times the noding grid. Trimming a tracer against a lake leaves a line's worth of
    // rounding behind, a millimetre or two of it, which is not worth reporting; anything
    // above this is a real stretch of border still sitting in the water.
    private const double ResidueTolerance = 1E-3;

    // How far past the shore to clip, in SVG units: about 6 m, far below the drawing's own
    // resolution of roughly 1.75 units between points.
    private const double ClipMargin = 1E-3;

    private readonly CoordinateSequenceFactory coordinateSequenceFactory;

    private readonly GeometryFactory geometryFactory;

    public CoastlineService(GeometryFactory geometryFactory)
    {
        ArgumentNullException.ThrowIfNull(geometryFactory);

        this.geometryFactory = geometryFactory;
        this.coordinateSequenceFactory = geometryFactory.CoordinateSequenceFactory;
    }

    // Returns the area the faces traced from this graph have to come to, which is what
    // Phase 4 checks itself against: the land, plus every hole that was discarded as a gap
    // since nothing bounds those any more, plus every lake, since a lake is a face of its
    // own as well as a hole in the land around it.
    public double AddArcs(IEnumerable<Polygon> polygons, ArcGraph arcGraph)
    {
        ArgumentNullException.ThrowIfNull(polygons);
        ArgumentNullException.ThrowIfNull(arcGraph);

        // Only the sea-facing boundary of the landmass is wanted. Internal borders are
        // governed by the tracer arcs, so unioning the countries is what discards the
        // shapes' own disagreeing versions of them.
        Polygon[] land = ExtractPolygons(
                UnaryUnionNG.Union(
                    polygons.ToArray(),
                    this.geometryFactory,
                    this.geometryFactory.PrecisionModel))
            .ToArray();

        List<Polygon> lakes = [];
        int lakeCount = 0;
        int gapCount = 0;
        double gapArea = 0.0;
        int ambiguousCount = 0;
        foreach (Polygon polygon in land)
        {
            arcGraph.AddArc(ArcKind.Coast, polygon.Shell.CoordinateSequence);

            foreach (LinearRing hole in polygon.Holes)
            {
                double ratio = this.GetAreaToLengthRatio(hole);
                if ((ratio > GapRatioUpperBound) && (ratio < LakeRatioLowerBound))
                {
                    ++ambiguousCount;
                }

                if (ratio < MaximumGapAreaToLengthRatio)
                {
                    ++gapCount;
                    gapArea += this.geometryFactory.CreatePolygon(hole).Area;
                }
                else
                {
                    arcGraph.AddArc(ArcKind.Lake, hole.CoordinateSequence);
                    lakes.Add(this.geometryFactory.CreatePolygon(hole));
                    ++lakeCount;
                }
            }
        }

        (int clipped, double removed) = this.ClipTracers(arcGraph, lakes);

        Log.Information(
            "Built {ShellCount} coast shells, kept {LakeCount} lakes and discarded {GapCount} holes as gaps, "
            + "enclosing {Area:0.0} square units of land and {LakeArea:0.0} of lake. Trimmed {Clipped} "
            + "tracers back to the lake shore, "
            + "removing {Removed:0.00} units of line.",
            land.Length,
            lakeCount,
            gapCount,
            land.Sum(polygon => polygon.Area),
            lakes.Sum(lake => lake.Area),
            clipped,
            removed);

        if (ambiguousCount > 0)
        {
            Log.Warning(
                "{AmbiguousCount} holes have an area to length ratio between {Lower} and {Upper}, a band that was "
                + "empty when the gap threshold was chosen. Lakes and gaps are no longer cleanly separated.",
                ambiguousCount,
                GapRatioUpperBound,
                LakeRatioLowerBound);
        }

        return land.Sum(polygon => polygon.Area) + gapArea + lakes.Sum(lake => lake.Area);
    }

    // A piece that still ends where the arc did keeps the arc's node; a piece that ends at
    // the water's edge gets a new one, which welding then attaches to the lake's ring.
    private static Node GetNode(ArcGraph arcGraph, Arc arc, Coordinate coordinate)
    {
        if (arc.StartNode.Coordinate.Equals2D(coordinate))
        {
            return arc.StartNode;
        }

        return arc.EndNode.Coordinate.Equals2D(coordinate) ? arc.EndNode : arcGraph.AddNode(coordinate);
    }

    private static IEnumerable<LineString> GetLines(Geometry geometry)
    {
        return geometry switch
            {
                GeometryCollection collection => collection.Geometries.SelectMany(GetLines),
                LineString line => [line],
                _ => [],
            };
    }

    private static IEnumerable<Polygon> ExtractPolygons(Geometry geometry)
    {
        return geometry switch
            {
                GeometryCollection geometryCollection => geometryCollection.OfType<Polygon>(),
                Polygon polygon => [polygon],
                _ => [],
            };
    }

    // Borders are drawn over lakes rather than stopping at them: 136 tracers in the sample
    // map reach inside one, though never by more than 0.783 units and mostly by less than
    // 0.05. Left alone they shave slivers off the lake when the faces are traced, so a lake
    // ends up partly owned by the countries around it. Cutting each tracer back to the shore
    // here, before welding, means the trimmed end is welded onto the lake ring instead and
    // the lake stays a single face.
    private (int Clipped, double Removed) ClipTracers(ArcGraph arcGraph, List<Polygon> lakes)
    {
        if (lakes.Count == 0)
        {
            return (0, 0.0);
        }

        STRtree<Polygon> lakeIndex = new STRtree<Polygon>();
        foreach (Polygon lake in lakes)
        {
            lakeIndex.Insert(lake.EnvelopeInternal, lake);
        }

        Arc[] originals = arcGraph.Arcs.ToArray();
        arcGraph.Arcs.Clear();
        int clipped = 0;
        double removed = 0.0;
        foreach (Arc arc in originals)
        {
            if (arc.Kind is not (ArcKind.Border or ArcKind.Substate))
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            LineString line = this.geometryFactory.CreateLineString(arc.GetFullCoordinates());
            Polygon[] overlapping = lakeIndex.Query(line.EnvelopeInternal).ToArray();
            if (overlapping.Length == 0)
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            // Against a slightly grown lake, not the lake itself. A border that follows the
            // shore sits within rounding of it rather than cleanly inside, so differencing
            // the lake as drawn leaves it in place: it then appears twice over, once as the
            // lake shore and once as the border, and the face traversal can pick up either.
            // Growing by a hair takes those with it. A border that merely ends at the shore
            // is pulled back by the same hair and welded onto the ring again in Phase 3.
            Geometry trimmed = overlapping.Aggregate(
                (Geometry)line,
                (current, lake) => current.IsEmpty ? current : current.Difference(lake.Buffer(ClipMargin)));
            if (Math.Abs(trimmed.Length - line.Length) < DegenerateLength)
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            ++clipped;
            removed += line.Length - trimmed.Length;
            foreach (LineString piece in GetLines(trimmed).Where(piece => piece.NumPoints >= 2))
            {
                arcGraph.AddArc(
                    arc.Kind,
                    GetNode(arcGraph, arc, piece.GetCoordinateN(0)),
                    GetNode(arcGraph, arc, piece.GetCoordinateN(piece.NumPoints - 1)),
                    this.coordinateSequenceFactory.Create(piece.Coordinates[1..^1]));
            }
        }

        // No tracer may be left inside a lake, or it will shave a sliver off when the faces
        // are traced and the lake will end up partly owned by a country.
        double[] residues = arcGraph.Arcs.Where(arc => arc.Kind is ArcKind.Border or ArcKind.Substate)
            .Select(arc => this.geometryFactory.CreateLineString(arc.GetFullCoordinates()))
            .Select(line => lakeIndex.Query(line.EnvelopeInternal)
                .Sum(lake => line.Intersection(lake).Length))
            .Where(residue => residue > ResidueTolerance)
            .OrderByDescending(residue => residue)
            .ToArray();

        if (residues.Length > 0)
        {
            Log.Warning(
                "{Remaining} tracers still run inside a lake after trimming, by {Total:0.####} units in all, "
                + "the worst by {Worst:0.####}.",
                residues.Length,
                residues.Sum(),
                residues[0]);
        }

        return (clipped, removed);
    }

    private double GetAreaToLengthRatio(LinearRing hole)
    {
        // A degenerate ring would divide by zero and come out as NaN, which compares false
        // against the threshold and would be kept as a lake. Treat it as a gap instead.
        Polygon polygon = this.geometryFactory.CreatePolygon(hole);
        return polygon.Length < DegenerateLength ? 0.0 : polygon.Area / polygon.Length;
    }
}
