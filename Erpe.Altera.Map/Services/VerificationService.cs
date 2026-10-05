// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

using Serilog;

public sealed class VerificationService : IVerificationService
{
    // What share of the boundary may have only one side to it before this is a break rather
    // than a residue. It is not zero, and that is an open question rather than a design: on
    // this map 11,785 segments of boundary, 0.878% of it, are neither coast nor lake shore
    // nor the edge of a face left out in 6a, and where they come from is not yet known. The
    // share rises to 4.882% in the coarsest output, not because simplification breaks
    // anything - the count falls to 4,889 - but because simplification takes away the
    // paired boundary much faster than the unpaired.
    //
    // So this is set to catch a break and not the residue: twice the worst figure a sound
    // run produces, against the better part of the boundary that a real break produces -
    // thinning every ring on its own, which is what per-polygon simplification does, loses
    // three quarters of all sharing.
    private const double MaximumUnpairedShare = 0.10;

    public SharingReport VerifySharing(IEnumerable<Country> countries, IEnumerable<Coordinate[]>? coastLines)
    {
        ArgumentNullException.ThrowIfNull(countries);

        // Keyed on the coordinates themselves, compared exactly. That is the whole point:
        // two countries either side of one border have to carry the same numbers, not
        // numbers within a tolerance of each other.
        Dictionary<Segment, Uses> uses = new Dictionary<Segment, Uses>();
        int segments = 0;
        foreach (Country country in countries)
        {
            foreach (Polygon polygon in country.Shapes.Select(shape => shape.Polygon))
            {
                segments += Add(uses, polygon.Shell, country, false);
                foreach (LinearRing hole in polygon.Holes)
                {
                    segments += Add(uses, hole, country, true);
                }
            }
        }

        HashSet<Segment> coast = GetCoast(coastLines);
        int unpaired = coastLines == null
            ? 0
            : uses.Count(pair => (pair.Value.Count == 1) && !coast.Contains(pair.Key));
        SharingReport report = new SharingReport(
            segments,
            uses.Values.Count(use => use.IsShared),
            uses.Values.Count(use => use.IsSameDirection),
            uses.Values.Count(use => use.IsOwnCountry),
            uses.Values.Count(use => use.Count == 1),
            uses.Values.Count(use => use.Count > 2),
            coast.Count,
            unpaired);

        Report(report, coastLines != null);

        return report;
    }

    // Rings are wound whichever way the face they came from was traced, and the writer
    // only puts them right as the file is written. Here they are oriented first - a shell
    // counter-clockwise and a hole clockwise, so that every ring is walked with the
    // country on its left - because otherwise two neighbours walking their shared border
    // the same way says nothing about which side either of them is on.
    private static int Add(Dictionary<Segment, Uses> uses, LinearRing ring, Country country, bool isHole)
    {
        Coordinate[] coordinates = ring.CoordinateSequence.ToCoordinateArray();
        bool reverse = GetSignedArea(coordinates) < 0.0 != isHole;
        int added = 0;
        for (int i = 0; i < (coordinates.Length - 1); ++i)
        {
            Coordinate start = coordinates[i];
            Coordinate end = coordinates[i + 1];
            if (start.Equals2D(end))
            {
                continue;
            }

            ++added;
            bool ordered = (start.X < end.X) || ((start.X <= end.X) && (start.Y < end.Y));
            Segment segment = GetSegment(start, end);
            bool forward = ordered != reverse;
            uses[segment] = uses.TryGetValue(segment, out Uses existing)
                ? existing.With(country, forward)
                : new Uses(country, forward);
        }

        return added;
    }

    private static double GetSignedArea(Coordinate[] coordinates)
    {
        double total = 0.0;
        for (int i = 0; i < (coordinates.Length - 1); ++i)
        {
            total += (coordinates[i].X * coordinates[i + 1].Y) - (coordinates[i + 1].X * coordinates[i].Y);
        }

        return total / 2.0;
    }

    // The coast as the same canonical segments the countries are counted in, so that a
    // segment used once can be asked whether it is coast or an unpaired border.
    private static HashSet<Segment> GetCoast(IEnumerable<Coordinate[]>? coastLines)
    {
        HashSet<Segment> coast = [];
        foreach (Coordinate[] line in coastLines ?? [])
        {
            for (int i = 0; i < (line.Length - 1); ++i)
            {
                if (!line[i].Equals2D(line[i + 1]))
                {
                    coast.Add(GetSegment(line[i], line[i + 1]));
                }
            }
        }

        return coast;
    }

    private static Segment GetSegment(Coordinate start, Coordinate end)
    {
        return ((start.X < end.X) || ((start.X <= end.X) && (start.Y < end.Y)))
            ? new Segment(start.X, start.Y, end.X, end.Y)
            : new Segment(end.X, end.Y, start.X, start.Y);
    }

    private static void Report(SharingReport report, bool hasCoast)
    {
        Log.Information(
            "Checked sharing over {Segments} segments: {Shared} are used by two countries from both sides with "
            + "the same coordinates, {UsedOnce} are used once, {SameDirection} are shared but wound the same way "
            + "and {OwnCountry} are used twice by one country.",
            report.Segments,
            report.Shared,
            report.UsedOnce,
            report.SameDirection,
            report.OwnCountry);

        if (report.Excess > 0)
        {
            Log.Error(
                "{Excess} segments are used by three or more rings at once, so the countries there overlap "
                + "rather than share a border.",
                report.Excess);
        }

        if (!hasCoast)
        {
            return;
        }

        if (report.UnpairedShare > MaximumUnpairedShare)
        {
            Log.Error(
                "{Unpaired} of the {UsedOnce} segments used once are neither coast nor lake shore nor the edge "
                + "of a face left out, which is {UnpairedShare:0.000%} of the boundary. Borders are not being "
                + "shared.",
                report.Unpaired,
                report.UsedOnce,
                report.UnpairedShare);

            return;
        }

        Log.Information(
            "Of the {Segments} segments, {Shared} are shared exactly and {OneSided} have one side to them, of "
            + "which {Unpaired} ({UnpairedShare:0.000%} of the boundary) are neither coast nor lake shore nor "
            + "the edge of a face left out and remain unaccounted for.",
            report.Segments,
            report.Shared,
            report.UsedOnce,
            report.Unpaired,
            report.UnpairedShare);
    }

    // One segment, as the two coordinates that bound it, ordered so that both sides of a
    // border produce the same key however each of them walks it.
    private readonly record struct Segment(double StartX, double StartY, double EndX, double EndY);

    // The countries that use one segment. Only the first two are kept by name, which is
    // all any of the reports need; past that only the count matters.
    private readonly record struct Uses(Country First, bool FirstForward, Country? Second, bool SecondForward, int Count)
    {
        public Uses(Country country, bool forward)
            : this(country, forward, null, false, 1)
        {
        }

        public bool IsOwnCountry => (this.Count == 2) && (this.First == this.Second);

        public bool IsSameDirection => (this.Count == 2)
            && (this.First != this.Second)
            && (this.FirstForward == this.SecondForward);

        public bool IsShared => (this.Count == 2)
            && (this.First != this.Second)
            && (this.FirstForward != this.SecondForward);

        public Uses With(Country country, bool forward)
        {
            return this.Count == 1
                ? new Uses(this.First, this.FirstForward, country, forward, 2)
                : new Uses(this.First, this.FirstForward, this.Second, this.SecondForward, this.Count + 1);
        }
    }
}
