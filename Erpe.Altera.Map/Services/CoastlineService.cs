// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Algorithm;
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

    // How close a run of shoreline has to come back to where it started to count as having
    // doubled back on itself. It is also the most that filling one can move the shoreline,
    // since the run is replaced by the vertex it set out from, and half a unit is under a
    // third of the drawing's own resolution.
    private const double MaximumSlitWidth = 0.5;

    // How long the out and back has to be. This is what keeps the coastline's own jitter out.
    // Scanning for runs of a fifth of a unit and up finds 51,923 of them that enclose sea
    // with a small radius, and 51,541 of those are shorter than 0.6 units: two consecutive
    // drawn segments folding onto each other. The population is down to two by one unit,
    // which is where the gaps between shapes begin.
    private const double MinimumSlitLength = 1.0;

    // How far ahead to look for the way back, which is what keeps the scan quadratic in
    // practice rather than over the whole of a ring that can run to 118,800 points. The
    // longest gap filled is reported, so a figure creeping up towards this one is the sign
    // that the search is being cut short.
    private const double MaximumSlitSearch = 50.0;

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
        List<(double Radius, double Length)> slits = [];
        List<double> keptInlets = [];
        int lakeCount = 0;
        int gapCount = 0;
        double gapArea = 0.0;
        int ambiguousCount = 0;
        foreach (Polygon polygon in land)
        {
            // Only the shells. A gap enclosed in a lake is a hole of the lake's own and not
            // what this is for, and the sign that tells a slit from a spit is the other way
            // round on a ring that bounds water rather than land.
            arcGraph.AddArc(
                ArcKind.Coast,
                this.coordinateSequenceFactory.Create(FillSlits(polygon.Shell, slits, keptInlets)));

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

        if (slits.Count > 0)
        {
            // Both ends of the threshold, every run, because the threshold cuts through a
            // population rather than a gap in one. If these two ever stop bracketing it
            // closely the shape of that population has changed and the figure wants
            // re-measuring rather than nudging.
            Log.Information(
                "Filled {SlitCount} gaps between shapes that reach the sea rather than being enclosed as a hole, "
                + "{SlitLength:0.00} units of shoreline in all and the longest of them {Longest:0.00}. The "
                + "widest filled measures {Widest:0.#####} on the area to length ratio the holes are judged by, "
                + "the narrowest inlet left alone {Narrowest:0.#####}.",
                slits.Count,
                slits.Sum(slit => slit.Length),
                slits.Max(slit => slit.Length),
                slits.Max(slit => slit.Radius),
                keptInlets.Count == 0 ? MaximumGapAreaToLengthRatio : keptInlets.Min());
        }

        if (ambiguousCount > 0)
        {
            Log.Warning(
                "{AmbiguousCount} holes have an area to length ratio between {Lower} and {Upper}, a band that was "
                + "empty when the gap threshold was chosen. Lakes and gaps are no longer cleanly separated.",
                ambiguousCount,
                GapRatioUpperBound,
                LakeRatioLowerBound);
        }

        // The ground the filled slits used to take out of the land is land again now, and the
        // hydraulic radius times the length of a slit is the area it covered.
        return land.Sum(polygon => polygon.Area)
            + slits.Sum(slit => slit.Radius * slit.Length)
            + gapArea
            + lakes.Sum(lake => lake.Area);
    }

    // A gap between two country shapes is a hole in the union and the test above throws it
    // away, unless it happens to reach the sea: then it is not a hole at all but a slit in
    // the shell, which the union walks up one wall of and back down the other. Nothing
    // encloses the ground beside such a slit, so the border drawn along the same line gets
    // traced into the outline of the landmass rather than into a face, and the countries on
    // either side of it are left with no ground at all. That is what left Maharath and
    // Sanjan with nothing: Maharath's shape is drawn 0.03 units off Sanjan's for the first
    // 3.77 units inland from the coast they share, and the wedge between the two opens onto
    // the sea.
    //
    // Three tests, cheapest first. The run has to come back to within MaximumSlitWidth of
    // where it started, it has to be at least MinimumSlitLength long, and it has to enclose
    // sea rather than land. The last is what does most of the work: of the 9352 runs on this
    // map that come back on themselves at all, 8778 enclose land and are headlands, spits and
    // narrow capes that have to be left exactly as they are, and 574 enclose sea.
    //
    // What is left after those three is judged by the hydraulic radius of Phase 2, because a
    // gap is a gap whether it is enclosed as a hole or open to the sea. Here, though, that
    // figure is a judgement and not a measurement: those 574 runs have radii running all the
    // way from 0.0005 to 1.0, thickest between 0.07 and 0.12, and they thin out across the
    // threshold rather than stopping - 18 of them in the 0.02 below 0.15 and 6 in the 0.02
    // above, the widest filled measuring 0.1474 against the 0.1522 of the narrowest left
    // alone. There is no empty valley to put the line in, as there is for the holes.
    //
    // What makes the figure safe is that the answer does not depend on it. Run at 0.12, 0.15
    // and 0.20 the number of gaps filled moves from 460 to 505 to 508, and every number that
    // matters is identical: 47 arcs separating nothing, 0.301% of the land inside no face, 8
    // countries without geometry. The gaps that cost a country its ground are far below any
    // of those thresholds.
    //
    // Known limitation: a slit straddling the ring's own first vertex is cut in part rather
    // than whole, because the closing vertex is never removed and the scan does not wrap.
    // Nothing here detects that case, so it is not known whether this map has one.
    private static Coordinate[] FillSlits(
        LinearRing ring,
        List<(double Radius, double Length)> slits,
        List<double> kept)
    {
        List<Coordinate> coordinates = [.. ring.Coordinates];
        bool counterClockwise = Orientation.IsCCW(ring.Coordinates);
        int i = 0;
        while (i < (coordinates.Count - 2))
        {
            int found = -1;
            double radius = 0.0;
            double length = 0.0;
            double run = 0.0;
            double area = 0.0;
            for (int j = i + 1; j < (coordinates.Count - 1); ++j)
            {
                run += coordinates[j - 1].Distance(coordinates[j]);
                area += (coordinates[j - 1].X * coordinates[j].Y) - (coordinates[j].X * coordinates[j - 1].Y);
                if (run > MaximumSlitSearch)
                {
                    break;
                }

                if ((run < MinimumSlitLength) || (coordinates[i].Distance(coordinates[j]) > MaximumSlitWidth))
                {
                    continue;
                }

                // Closing the loop before taking its area is not optional. The shoelace
                // terms are twice the signed area of a triangle with the origin, so an
                // unclosed chain out at x = 4412 carries a residual of some 150 square
                // units where the slit itself covers 0.11.
                double enclosed = (area
                    + ((coordinates[j].X * coordinates[i].Y) - (coordinates[i].X * coordinates[j].Y))) / 2.0;
                double ratio = Math.Abs(enclosed) / run;
                if ((enclosed > 0.0) != counterClockwise)
                {
                    if (ratio <= MaximumGapAreaToLengthRatio)
                    {
                        found = j;
                        radius = ratio;
                        length = run;
                    }
                    else
                    {
                        kept.Add(ratio);
                    }
                }

                // The first return and no other. A slit's two walls are next to each other
                // in the ring, so the first place it comes back to where it started, once it
                // has gone anywhere at all, is the other wall; if what comes back first is
                // something else then this is not the mouth of a slit, and carrying on to
                // look for a thin loop further along would be looking for one that is not
                // there. On this map it makes no difference either way, both ways round
                // filling the same 505.
                break;
            }

            // A ring needs four coordinates to enclose anything, so a cut that would take it
            // below that is no cut at all.
            if ((found < 0) || ((coordinates.Count - (found - i)) < 4))
            {
                ++i;
                continue;
            }

            slits.Add((radius, length));
            coordinates.RemoveRange(i + 1, found - i);
        }

        return [.. coordinates];
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
