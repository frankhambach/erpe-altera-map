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
    // How far the segments used once may fall short of the coast before the shortfall
    // means the borders are not being shared.
    //
    // Measured both ways. On this map the shortfall is 0.152%, which is the four faces
    // dropped for a self-crossing ring; with the sharing deliberately broken - every ring
    // thinned on its own, which is the failure per-polygon simplification causes - it is
    // 34.057%. Anything from half a percent to thirty would separate those two cases
    // identically, so this sits in the middle of a very wide valley.
    private const double MaximumShortfall = 0.02;

    public SharingReport VerifySharing(IEnumerable<Country> countries, ArcGraph arcGraph, bool coversTheLand)
    {
        ArgumentNullException.ThrowIfNull(countries);
        ArgumentNullException.ThrowIfNull(arcGraph);

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

        SharingReport report = new SharingReport(
            segments,
            uses.Values.Count(use => use.IsShared),
            uses.Values.Count(use => use.IsSameDirection),
            uses.Values.Count(use => use.IsOwnCountry),
            uses.Values.Count(use => use.Count == 1),
            uses.Values.Count(use => use.Count > 2),
            coversTheLand ? CountCoastSegments(arcGraph) : 0);

        Report(report, coversTheLand);

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
            Segment segment = ordered
                ? new Segment(start.X, start.Y, end.X, end.Y)
                : new Segment(end.X, end.Y, start.X, start.Y);
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

    private static int CountCoastSegments(ArcGraph arcGraph)
    {
        return arcGraph.Arcs.Where(arc => arc.Kind is ArcKind.Coast or ArcKind.Lake)
            .Sum(arc => arc.GetFullCoordinates().Length - 1);
    }

    private static void Report(SharingReport report, bool coversTheLand)
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

        if (!coversTheLand)
        {
            return;
        }

        if (Math.Abs(report.Shortfall) > MaximumShortfall)
        {
            Log.Error(
                "The {UsedOnce} segments used once should be the coast and the lake shores, of which the graph "
                + "has {CoastSegments}. A difference of {Shortfall:0.000%} is far more than the faces left out "
                + "of the output can account for, so borders are not being shared exactly.",
                report.UsedOnce,
                report.CoastSegments,
                report.Shortfall);

            return;
        }

        Log.Information(
            "The {UsedOnce} segments used once account for the {CoastSegments} coast and lake segments in the "
            + "graph to {Shortfall:0.000%}, so what is not shared is the coast and borders are shared exactly.",
            report.UsedOnce,
            report.CoastSegments,
            report.Shortfall);
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
