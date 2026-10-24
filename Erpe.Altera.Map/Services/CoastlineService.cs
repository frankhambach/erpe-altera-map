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
using NetTopologySuite.Geometries.Prepared;
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

    // How far a border tracer may run through a hole, against the hole's own half-perimeter -
    // its length, for a hole thin enough to have one - before the hole is a gap between two
    // country shapes rather than a lake. A gap along a border has the border running its whole
    // length; a lake has at most one poking into it.
    //
    // Measured over the 462 holes the ratio above keeps as lakes: two of them come to 18.88
    // and 8.19, and the rest top out at 1.88, a span of 4.4x with nothing in it. Those two are
    // the gap between Padun and Kunmin, 2.49 square units with 3.71 units of border running
    // along it, and a second of 0.98 square units with 1.92 units along it. Both are gaps.
    //
    // Known limitation: the measure is elongation-aware, which is the point - a tracer running
    // the length of a sliver scores far higher than one crossing a blob - but a compact lake
    // crossed straight through would score about 4, so the figure sits above that rather than
    // at the middle of the measured span. Nothing on this map comes near: the highest genuine
    // lake is 1.88.
    private const double MaximumLakeTracerCrossing = 6.0;

    private const double DegenerateLength = 1E-9;

    // Ten times the noding grid. Trimming a tracer against a lake leaves a line's worth of
    // rounding behind, a millimetre or two of it, which is not worth reporting; anything
    // above this is a real stretch of border still sitting in the water.
    private const double ResidueTolerance = 1E-3;

    // How far past the shore to clip, in SVG units: about 6 m, far below the drawing's own
    // resolution of roughly 1.75 units between points.
    private const double ClipMargin = 1E-3;

    // How close a run of ring has to come back to where it started to count as having doubled
    // back on itself rather than gone round something.
    //
    // The five needles on this map return to within 0.0002, 0.00036, 0.00103, 0.00106 and
    // 0.00275 units of where they set out, which is two shapes disagreeing about the water's
    // edge by between a millimetre and two centimetres on the ground. Ten times the widest of
    // those leaves room for a worse drawing without reaching anything real: the next run in,
    // which returns exactly and is kept, is told apart by what it encloses rather than by this.
    //
    // It cannot be loosened much further, and 3c's 0.5 units is far too loose for a test
    // with no length behind it: at half a unit a ring's own jitter qualifies, 267 runs that
    // enclose exactly nothing over 0.04 units, which is two drawn segments folding onto each
    // other. Removing those would be removing the drawing.
    private const double MaximumNeedleReturn = 0.03;

    // What such a run may enclose, as area over the length of the loop it closes, before it is a
    // shape rather than a needle.
    //
    // Measured over the 2466 runs that come back within the tolerance above: five enclose
    // between 9.2e-06 and 1.005e-04, the next encloses 8.733e-04, and the rest run up to 0.95.
    // This sits at the middle of that gap on a log scale, 3x above the widest needle and 3x
    // below the narrowest ring that is kept.
    //
    // All five are lake shores, and that is not luck: 3c has already taken the shells'
    // thin slits, so what is left on a shell is the fat ones it would not fill, while nothing
    // has ever been taken off a hole.
    private const double MaximumNeedleRatio = 3E-4;

    // How far ahead to look for the way back, which is what keeps this scan off the whole of a
    // ring that can run to 118,800 points. 3c looks 50 units ahead because a slit can be
    // long; a needle is one line drawn twice and the longest here runs 0.937 units, so five
    // times that is ample. Measured: the scan costs about 3 seconds of a 100 second run at this
    // bound and about 7 at 3c's 50, for the same six needles.
    //
    // The longest needle cut is reported every run for this reason: a figure creeping up
    // towards this one is the sign that the search is being cut short.
    private const double MaximumNeedleSearch = 5.0;

    // How much of a tracer has to lie within that margin of the shore before the run is a
    // border drawn along the water's edge rather than one that merely ends at it.
    //
    // A border reaching the coast crosses the margin at some angle and loses a sliver of its
    // last segment to it, and the shallower the approach the longer that sliver - the bound is
    // the margin over the sine of the angle, so it is a property of the drawing and not a
    // defect. Measured over the 321 tracers that lose anything at all to the margin on this
    // map: 304 lose under 0.01 units, a tail of 8 reaches 0.093, which is an approach of half a
    // degree, and then the nine runs that follow the shore start again at 0.159 and go to 1.415.
    //
    // So this sits in a gap of 1.7x rather than in an empty decade, and what makes it safe is
    // the geometry either side of it rather than the width of the gap: below it is one segment's
    // worth of crossing the shore, above it a stretch drawn along the shore. Clipping without a
    // minimum at all was measured and is not harmless - it cuts all 321, and the welding that
    // re-attaches those pulled-back ends leaves Lebanon and Osurfine with no geometry, fuses
    // Abeline and Osurfine into one face of 5300 units and takes the arcs that separate nothing
    // from 1 to 4. Re-measured against this pipeline rather than the one it was written for,
    // since 4d has since taken out one of the reasons it hurt.
    private const double MinimumShoreRun = 0.1;

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
    // step 5 checks itself against: the land, plus every hole that was discarded as a gap
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

        // Built once, before the holes are judged, and from the tracers as drawn: 3f has
        // not trimmed them yet, so a border that runs through a gap is still all there.
        STRtree<LineString> tracers = new STRtree<LineString>();
        foreach (Arc arc in arcGraph.Arcs.Where(arc => arc.Kind is ArcKind.Border or ArcKind.Substate))
        {
            LineString line = this.geometryFactory.CreateLineString(arc.GetFullCoordinates());
            tracers.Insert(line.EnvelopeInternal, line);
        }

        List<Polygon> lakes = [];
        List<Coordinate[]> shells = [];
        List<(double Radius, double Length)> slits = [];
        List<double> necks = [];
        int pieces = 0;
        List<(double Radius, double Length)> needles = [];
        List<double> keptInlets = [];
        int lakeCount = 0;
        int gapCount = 0;
        double gapArea = 0.0;
        int ambiguousCount = 0;
        int crossedCount = 0;
        foreach (Polygon polygon in land)
        {
            // Only the shells. A gap enclosed in a lake is a hole of the lake's own and not
            // what this is for, and the sign that tells a slit from a spit is the other way
            // round on a ring that bounds water rather than land.
            // Needles after the slits rather than before, so that a run the fill leaves behind
            // is still asked about. Neither makes work for the other on this map.
            Coordinate[] shell = RemoveNeedles(FillSlits(polygon.Shell, slits, keptInlets), needles);
            shells.Add(shell);
            arcGraph.AddArc(ArcKind.Coast, this.coordinateSequenceFactory.Create(shell));

            // Each hole in pieces, divided wherever it narrows to a neck - see SplitAtNecks - and
            // each piece judged on its own as the whole hole was before.
            foreach (LinearRing hole in polygon.Holes.SelectMany(
                         drawn => this.SplitAtNecks(drawn, tracers, necks).Select(this.geometryFactory.CreateLinearRing)))
            {
                ++pieces;
                double ratio = this.GetAreaToLengthRatio(hole);
                if ((ratio > GapRatioUpperBound) && (ratio < LakeRatioLowerBound))
                {
                    ++ambiguousCount;
                }

                // Either test is enough to call a hole a gap, and each catches what the other
                // cannot. The ratio alone misses a gap wide enough to look like water, which is
                // what left Padun and Kunmin sharing a face. The crossing alone misses the 868
                // gaps with no tracer anywhere near them.
                double crossing = ratio < MaximumGapAreaToLengthRatio
                    ? 0.0
                    : this.GetTracerCrossing(hole, tracers, ratio);
                if (crossing > MaximumLakeTracerCrossing)
                {
                    ++crossedCount;
                }

                if ((ratio < MaximumGapAreaToLengthRatio) || (crossing > MaximumLakeTracerCrossing))
                {
                    ++gapCount;
                    gapArea += this.geometryFactory.CreatePolygon(hole).Area;
                }
                else
                {
                    // A lake shore is where the needles are: 3d only ever asked this ring
                    // whether it was water, and nothing has taken anything off it since.
                    Coordinate[] shore = RemoveNeedles(hole.CoordinateSequence.ToCoordinateArray(), needles);
                    arcGraph.AddArc(ArcKind.Lake, this.coordinateSequenceFactory.Create(shore));
                    lakes.Add(this.geometryFactory.CreatePolygon(this.geometryFactory.CreateLinearRing(shore)));
                    ++lakeCount;
                }
            }
        }

        (int clipped, double removed) = this.ClipTracers(arcGraph, lakes);
        (int shoreClipped, double shoreRemoved, int stranded, double discarded) =
            this.ClipShoreRuns(arcGraph, shells);

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

        if (crossedCount > 0)
        {
            Log.Information(
                "Discarded {CrossedCount} of those holes as gaps because a border runs through them rather than "
                + "merely into them, which the area to length ratio had taken for water.",
                crossedCount);
        }

        if (shoreClipped > 0)
        {
            Log.Information(
                "Cut {Clipped} tracers back off the shore, {Removed:0.00} units of line drawn along the water's "
                + "edge rather than up to it, and dropped {Stranded} pieces of {Discarded:0.00} units that the "
                + "cut left seaward of the coast.",
                shoreClipped,
                shoreRemoved,
                stranded,
                discarded);
        }

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

        if (necks.Count > 0)
        {
            Log.Information(
                "Divided the holes at {Necks} necks into {Pieces} pieces before judging them, the widest neck "
                + "{Widest:0.####} units across.",
                necks.Count,
                pieces,
                necks.Max());
        }

        if (needles.Count > 0)
        {
            Log.Information(
                "Cut {Needles} needles out of the coast and lake rings, {Length:0.####} units of line a ring had "
                + "already drawn once, the longest of them {Longest:0.####} units and the widest measuring "
                + "{Widest:0.0e+0} on the area to length ratio the rings are judged by.",
                needles.Count,
                needles.Sum(needle => needle.Length),
                needles.Max(needle => needle.Length),
                needles.Max(needle => needle.Radius));
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
    // What is left after those three is judged by the hydraulic radius of 3d, because a
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

    // The first neck along an open ring, as the index on one side of it and how many vertices on
    // the other lies, or (-1, -1). Taken round the ring, past its end and on from its start: a gap
    // straddling the vertex the ring happens to start at is a gap like any other, and the one on
    // Magallanica's lake did - FillSlits does not wrap and carries that as a known limitation, and
    // here it was what kept the gap on the lake once every other condition had been put right.
    private static (int I, int T) FindNeck(
        List<Coordinate> open,
        bool counterClockwise,
        Func<Coordinate, Coordinate, bool> isTraced)
    {
        int count = open.Count;
        for (int i = 0; i < count; ++i)
        {
            double run = 0.0;
            double area = 0.0;
            for (int t = 1; t < count; ++t)
            {
                Coordinate previous = open[(i + t - 1) % count];
                Coordinate current = open[(i + t) % count];
                run += previous.Distance(current);
                area += (previous.X * current.Y) - (current.X * previous.Y);
                if (run > MaximumSlitSearch)
                {
                    break;
                }

                if ((run < MinimumSlitLength) || (open[i].Distance(current) > MaximumSlitWidth))
                {
                    continue;
                }

                // Closed where the return comes nearest, not where it first comes within the
                // width: the ring is followed on for as long as it stays within it. A gap a hair
                // wide is first within half a unit of its mouth half a unit down its far wall, and
                // the line closing it there runs along the gap at so shallow an angle that the
                // walls' own wiggles can cross it. Straight across the mouth they do not.
                int best = t;
                double bestArea = area;
                double sum = area;
                for (int u = t + 1; (u < count) && (open[i].Distance(open[(i + u) % count]) <= MaximumSlitWidth); ++u)
                {
                    Coordinate before = open[(i + u - 1) % count];
                    Coordinate at = open[(i + u) % count];
                    sum += (before.X * at.Y) - (at.X * before.Y);
                    if (open[i].Distance(at) < open[i].Distance(open[(i + best) % count]))
                    {
                        (best, bestArea) = (u, sum);
                    }
                }

                // Both pieces need three points to enclose anything, and the one closed by the
                // neck must enclose the hole's inside rather than a spit of land.
                int j = (i + best) % count;
                double enclosed = (bestArea + ((open[j].X * open[i].Y) - (open[i].X * open[j].Y))) / 2.0;
                bool isWater = (enclosed > 0.0) == counterClockwise;
                bool isProper = ((best + 1) >= 3) && ((count - best + 1) >= 3);
                if (isWater && isProper && !IsCrossed(open, i, j) && isTraced(open[i], open[j]))
                {
                    return (i, best);
                }

                // The first return and no other, as in FillSlits.
                break;
            }
        }

        return (-1, -1);
    }

    // Whether the line between two vertices of an open ring crosses any segment of it that does
    // not end at either of them.
    private static bool IsCrossed(List<Coordinate> open, int i, int j)
    {
        LineIntersector intersector = new RobustLineIntersector();
        for (int k = 0; k < open.Count; ++k)
        {
            int next = (k + 1) % open.Count;
            if ((k == i) || (k == j) || (next == i) || (next == j))
            {
                continue;
            }

            intersector.ComputeIntersection(open[i], open[j], open[k], open[next]);
            if (intersector.HasIntersection)
            {
                return true;
            }
        }

        return false;
    }

    // A ring that leaves a vertex, goes somewhere and comes back to within a hair of it without
    // enclosing anything on the way has drawn one line twice rather than gone round something.
    // Nothing is on the far side of such a run, so whatever is assembled from the ring walks its
    // own line out and back - which is the segment the sharing check reports as used twice by one
    // country, Dematia's 0.47 units of lake shore at 3526.82, 2176.64.
    //
    // It is the same defect 3c fills on a shell, arriving where 3c cannot see it. The
    // fill asks for a unit of travel before it will take a run, because under that a shell's own
    // jitter qualifies - 51,541 runs of two drawn segments folding onto each other - and
    // Dematia's needle travels 0.937. What makes a length minimum unnecessary here is that the
    // run has to come back to within MaximumNeedleReturn rather than within half a unit: jitter
    // does not return to where it set out from, it wanders on.
    //
    // The run is replaced by the vertex it set out from, so the ring loses the needle and keeps
    // everything either side of it. This also runs on the lake rings, which is where all five of
    // the needles on this map are, and needs no winding test to do it: a run that encloses
    // nothing encloses nothing on either side, so unlike a slit it does not matter which side
    // the water is on.
    //
    // Known limitation, shared with 3c: a needle straddling the ring's own first vertex is
    // cut in part rather than whole, because the closing vertex is never removed and the scan
    // does not wrap.
    private static Coordinate[] RemoveNeedles(Coordinate[] ring, List<(double Radius, double Length)> needles)
    {
        List<Coordinate> coordinates = [.. ring];
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
                if (run > MaximumNeedleSearch)
                {
                    break;
                }

                if ((j == (i + 1)) || (coordinates[i].Distance(coordinates[j]) > MaximumNeedleReturn))
                {
                    continue;
                }

                // Closed before the area is taken, for the same reason 3c closes its loop:
                // the shoelace terms are twice the signed area of a triangle with the origin, so
                // an unclosed chain out at x = 3526 carries a residual thousands of times the
                // needle itself.
                double enclosed = Math.Abs(
                        area + ((coordinates[j].X * coordinates[i].Y) - (coordinates[i].X * coordinates[j].Y)))
                    / 2.0;
                double ratio = enclosed / run;
                if (ratio <= MaximumNeedleRatio)
                {
                    found = j;
                    radius = ratio;
                    length = run;
                }

                // The first return and no other, as in 3c: a needle's two legs are next to
                // each other in the ring, so the first place it comes back to where it started
                // is the other leg, and a wider loop further along is not this.
                break;
            }

            // A ring needs four coordinates to enclose anything, so a cut that would take it
            // below that is no cut at all.
            if ((found < 0) || ((coordinates.Count - (found - i)) < 4))
            {
                ++i;
                continue;
            }

            needles.Add((radius, length));
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

    // A hole divided wherever its ring comes back to within MaximumSlitWidth of itself around a
    // stretch of its own inside at least MinimumSlitLength long: the neck between a lake and a gap
    // running into it, or between two lakes a gap joins. Each piece is then judged on its own.
    //
    // A federation drawn as its substates is where this arises. Two substates that do not quite
    // meet leave a gap along their border, and where that border reaches a lake the gap and the
    // lake are one hole. Judged whole it was neither: the gap added its two walls to the lake's
    // perimeter and the border drawn along it ran through the hole, and three lakes on this map
    // were judged gaps and given to the land - of 495, 98 and 19 square units, with 46, 55 and 37
    // units of gap attached and the border crossing them 6.3, 31.1 and 50.8 times over. The two
    // smaller ones share one gap. Cutting the gap off the lake, as 3c cuts a slit off the coast,
    // is not enough for those two: seen from either lake, what runs off it is the gap and the
    // other lake, which is not thin. Dividing at the necks gives two lakes and a gap.
    //
    // The tests are 3c's: the first return to within the width, after a run of at least the
    // length and no more than the search, enclosing the inside of the hole and not a spit of land
    // poking into it. A neck is only taken where the line closing it crosses no other part of the
    // ring, so that every piece is a ring of its own.
    //
    // And only where a tracer comes as near the neck as its walls come to each other. A gap
    // between two shapes lies along the border between them, so the border is at the neck it
    // makes with a lake - through it or beside it, since the border need not run down the gap's
    // middle - and a lake's own narrows have no border at them. Without this the rule took every
    // wiggle of a lake shore that comes back on itself: on the country shapes, where the gaps it
    // is for hardly arise, it changed 230 lakes by 9.4 square units between them and split one in
    // two. With it, it changes 11 there, by 0.09 square units between them, each a hairline of gap
    // along a border, and on the substates it gives back every lake the country shapes have.
    private List<Coordinate[]> SplitAtNecks(LinearRing ring, STRtree<LineString> tracers, List<double> necks)
    {
        bool counterClockwise = Orientation.IsCCW(ring.Coordinates);
        List<Coordinate[]> done = [];
        Stack<List<Coordinate>> work = new Stack<List<Coordinate>>();
        work.Push([.. ring.Coordinates.SkipLast(1)]);
        while (work.Count > 0)
        {
            List<Coordinate> open = work.Pop();
            (int i, int t) = FindNeck(open, counterClockwise, (start, end) => this.IsTraced(start, end, tracers));
            if (i < 0)
            {
                done.Add([.. open, open[0]]);
                continue;
            }

            int count = open.Count;
            necks.Add(open[i].Distance(open[(i + t) % count]));
            work.Push([.. Enumerable.Range(0, t + 1).Select(u => open[(i + u) % count])]);
            work.Push([.. Enumerable.Range(0, count - t + 1).Select(u => open[(i + t + u) % count])]);
        }

        return done;
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
            // is pulled back by the same hair and welded onto the ring again in step 4.
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

    // A border drawn along the coast rather than up to it is the same defect as a border drawn
    // through a lake, and it arrives by a different route. 3c fills the slits that open onto
    // the sea, but a slit the fill will not take survives in the shell as a hairline needle, and
    // where a tracer was drawn along the same line the shore and the border carry it twice over.
    // Both then bound the faces traced there, which is a segment three rings use at once: Khumer
    // and Siam over 1.4 units of one, Namiba over 0.5 of another. The two slits behind those
    // miss the fill for different reasons - one comes back after 0.64 units, under its minimum
    // of 1, and the other never comes back within 0.74 of its mouth, over its maximum of 0.5.
    //
    // Cut against the shore as the coast arcs carry it, which is after the slits are filled.
    // Cutting against the shells instead would take the borders that run up a filled slit, and
    // those are the real borders 3c exists to give back.
    //
    // Unlike a lake, the water here is not forbidden ground: the sea's edge is a line a border
    // legitimately ends at, and the coast is where it should. So only a run along the edge is
    // cut, and only one longer than MinimumShoreRun, which is what separates a border drawn in
    // the water from one crossing the margin on its way to the shore.
    //
    // What the cut leaves seaward of the coast bounds nothing and goes with it. One piece does
    // on this map, 0.4766 units past the tip of the needle it was drawn along.
    private (int Clipped, double Removed, int Stranded, double Discarded) ClipShoreRuns(
        ArcGraph arcGraph,
        List<Coordinate[]> shells)
    {
        STRtree<(Coordinate Start, Coordinate End)> shore = new STRtree<(Coordinate, Coordinate)>();
        STRtree<IPreparedGeometry> land = new STRtree<IPreparedGeometry>();
        PreparedGeometryFactory factory = new PreparedGeometryFactory();
        foreach (Coordinate[] shell in shells)
        {
            Polygon polygon = this.geometryFactory.CreatePolygon(shell);
            land.Insert(polygon.EnvelopeInternal, factory.Create(polygon));
            for (int i = 0; i < (shell.Length - 1); ++i)
            {
                shore.Insert(new Envelope(shell[i], shell[i + 1]), (shell[i], shell[i + 1]));
            }
        }

        Arc[] originals = arcGraph.Arcs.ToArray();
        arcGraph.Arcs.Clear();
        int clipped = 0;
        double removed = 0.0;
        int stranded = 0;
        double discarded = 0.0;
        foreach (Arc arc in originals)
        {
            if (arc.Kind is not (ArcKind.Border or ArcKind.Substate))
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            Coordinate[] coordinates = arc.GetFullCoordinates();
            LineString line = this.geometryFactory.CreateLineString(coordinates);
            Geometry margin = this.GetShoreMargin(coordinates, shore);
            Geometry trimmed = margin.IsEmpty ? line : line.Difference(margin);
            double run = line.Length - trimmed.Length;
            if (run < MinimumShoreRun)
            {
                arcGraph.Arcs.Add(arc);
                continue;
            }

            ++clipped;
            removed += run;

            // One by one, because a border drawn along the shore is a defect in the source map
            // rather than something this pipeline should have to carry: the slit the two share
            // is two shapes disagreeing about where the coast is, and the remedy is to draw them
            // the same.
            // What the difference took out, which is where the run is. It cannot be empty for a
            // run over the minimum, since the run is the length the difference removed, but the
            // report is not the thing that should fail if that minimum is ever lowered: at
            // 1e-09 it is, on a run of 0.0011 units that the overlay rounds away to nothing.
            Geometry inWater = line.Intersection(margin);
            Coordinate at = inWater.IsEmpty ? coordinates[0] : inWater.Coordinates[0];
            Log.Warning(
                "{Kind} arc {ArcId} runs along the coast for {Run:0.####} units from {X:0.##}, {Y:0.##}. The "
                + "shore there is a slit the border was drawn along; the stretch in the water was cut out.",
                arc.Kind,
                arc.Id,
                run,
                at.X,
                at.Y);

            foreach (LineString piece in GetLines(trimmed).Where(piece => piece.NumPoints >= 2))
            {
                if (!this.IsOnLand(piece, land))
                {
                    ++stranded;
                    discarded += piece.Length;
                    continue;
                }

                arcGraph.AddArc(
                    arc.Kind,
                    GetNode(arcGraph, arc, piece.GetCoordinateN(0)),
                    GetNode(arcGraph, arc, piece.GetCoordinateN(piece.NumPoints - 1)),
                    this.coordinateSequenceFactory.Create(piece.Coordinates[1..^1]));
            }
        }

        return (clipped, removed, stranded, discarded);
    }

    // The shore near one tracer, grown into the margin the tracer is cut against. Gathered per
    // segment rather than over the tracer's whole envelope, so that a border running past a bay
    // pulls in the shore it actually runs along and not the far side of the water.
    private Geometry GetShoreMargin(
        Coordinate[] coordinates,
        STRtree<(Coordinate Start, Coordinate End)> shore)
    {
        HashSet<(Coordinate Start, Coordinate End)> near = [];
        for (int i = 0; i < (coordinates.Length - 1); ++i)
        {
            Envelope envelope = new Envelope(coordinates[i], coordinates[i + 1]);
            envelope.ExpandBy(ClipMargin);
            foreach ((Coordinate Start, Coordinate End) segment in shore.Query(envelope))
            {
                near.Add(segment);
            }
        }

        return near.Count == 0
            ? LineString.Empty
            : this.geometryFactory
                .CreateMultiLineString(
                    [.. near.Select(segment => this.geometryFactory.CreateLineString([segment.Start, segment.End]))])
                .Buffer(ClipMargin);
    }

    // Which side of the shore a piece of a cut tracer lies on. It cannot cross the margin it was
    // cut against, so one point on it answers for all of it - the midpoint of its first segment,
    // since an end of it sits on the margin's own edge where the answer is a coin toss.
    //
    // The shells, holes and all: a hole in the land is either a lake, which the tracers have
    // already been clipped against, or a gap 3d discarded, and the ground a discarded gap
    // covers is land again.
    private bool IsOnLand(LineString piece, STRtree<IPreparedGeometry> land)
    {
        Coordinate first = piece.GetCoordinateN(0);
        Coordinate second = piece.GetCoordinateN(1);
        Point point = this.geometryFactory.CreatePoint(
            new Coordinate((first.X + second.X) / 2.0, (first.Y + second.Y) / 2.0));

        return land.Query(point.EnvelopeInternal).Any(shell => shell.Contains(point));
    }

    // Whether a border or substate tracer comes within MaximumSlitWidth of the line between two
    // points: as near as a gap's walls come to each other, which is as near as the border it lies
    // along has to come to them.
    private bool IsTraced(Coordinate start, Coordinate end, STRtree<LineString> tracers)
    {
        LineString line = this.geometryFactory.CreateLineString([start, end]);
        Envelope envelope = line.EnvelopeInternal.Copy();
        envelope.ExpandBy(MaximumSlitWidth);

        return tracers.Query(envelope).Any(tracer => tracer.IsWithinDistance(line, MaximumSlitWidth));
    }

    // How far the tracers run through a hole, against the hole's own half-perimeter. For a
    // hole thin enough to be a gap that half-perimeter is its length, so the measure says what
    // fraction of the gap the border follows; dividing instead by the hydraulic radius, which
    // is the same thing scaled, keeps the shape of the hole in the answer, and that is what
    // separates a border following a sliver from one crossing a pool.
    private double GetTracerCrossing(LinearRing hole, STRtree<LineString> tracers, double ratio)
    {
        if (ratio < DegenerateLength)
        {
            return 0.0;
        }

        Polygon polygon = this.geometryFactory.CreatePolygon(hole);
        double inside = tracers.Query(polygon.EnvelopeInternal)
            .Sum(tracer => tracer.Intersection(polygon).Length);

        return inside / ratio;
    }

    private double GetAreaToLengthRatio(LinearRing hole)
    {
        // A degenerate ring would divide by zero and come out as NaN, which compares false
        // against the threshold and would be kept as a lake. Treat it as a gap instead.
        Polygon polygon = this.geometryFactory.CreatePolygon(hole);
        return polygon.Length < DegenerateLength ? 0.0 : polygon.Area / polygon.Length;
    }
}
