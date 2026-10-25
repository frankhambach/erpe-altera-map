// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using NetTopologySuite.Index.Strtree;

using Serilog;

public sealed class VerificationService : IVerificationService
{
    // What share of the boundary may have only one side to it before this is a break rather
    // than a residue.
    //
    // This was 10% for as long as the residue was unexplained, which it no longer is. It stood
    // at 11,785 segments, 0.878% of the boundary, and the cause was the union 7a used to dissolve
    // a country's faces: a general overlay re-nodes what it is given and writes a new edge
    // wherever it decides two are not quite the same. Walking the arcs instead took that residue
    // to 5 segments, 0.000%, and the coarse outputs from 1.632% and 3.684% to 0.002% and 0.004%.
    //
    // So the figure can now be what it was always meant to be. 0.1% is 25 times the worst a
    // sound run produces and 340 times less than a real break: thinning every ring on its own,
    // which is what per-polygon simplification does, leaves 34% of the boundary unpaired.
    //
    // None remain. The last 3 were taken for the output's six decimals showing and were not: they
    // were Bruni's islet, which Kuti was written over without a hole for it, since the islet is
    // drawn with its node on Kuti's lake shore and was placed by that vertex alone.
    private const double MaximumUnpairedShare = 0.001;

    // How many of the shapes found inside a shape of the other kind are named in the log.
    private const int MaximumReported = 20;

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

    // Asked of a point inside each shape, which is enough for what this is for: a shape wholly
    // inside one of the other kind, an island a zone took no hole for. A shape that overlaps
    // another only in part shares no border with it where it should, and VerifySharing finds the
    // segments that has unpaired or used three times.
    public int VerifyDisjoint(IEnumerable<Country> land, IEnumerable<Country> zones)
    {
        ArgumentNullException.ThrowIfNull(land);
        ArgumentNullException.ThrowIfNull(zones);

        Country[] landCountries = [.. land];
        Country[] zoneCountries = [.. zones];
        List<string> inside =
        [
            .. FindInside(landCountries, zoneCountries, "land"),
            .. FindInside(zoneCountries, landCountries, "zone"),
        ];
        int shapes = landCountries.Sum(country => country.Shapes.Count) + zoneCountries.Sum(country => country.Shapes.Count);
        if (inside.Count > 0)
        {
            Log.Error(
                "{Inside} of the {Shapes} shapes of the land and the economic zones lie inside a shape of the other "
                + "kind, so the two overlap there: {Which}.",
                inside.Count,
                shapes,
                string.Join("; ", inside.Take(MaximumReported)));
        }
        else
        {
            Log.Information(
                "Checked the {Shapes} shapes of the land and the economic zones for overlap: none lies inside a shape of "
                + "the other kind.",
                shapes);
        }

        return inside.Count;
    }

    // Each shape of the one set that has a point inside it inside a shape of the other, named with
    // the shape it lies in.
    private static IEnumerable<string> FindInside(Country[] shapes, Country[] others, string kind)
    {
        STRtree<(Shape Shape, Country Country)> index = new STRtree<(Shape Shape, Country Country)>();
        foreach (Country country in others)
        {
            foreach (Shape shape in country.Shapes.Where(shape => !shape.Polygon.IsEmpty))
            {
                index.Insert(shape.Polygon.EnvelopeInternal, (shape, country));
            }
        }

        Dictionary<Shape, IPreparedGeometry> prepared = [];
        foreach ((Country country, Shape shape) in shapes.SelectMany(
                     country => country.Shapes.Where(shape => !shape.Polygon.IsEmpty).Select(shape => (country, shape))))
        {
            // The point as the algorithm finds it. Polygon.InteriorPoint puts it on the factory's
            // precision model, which is Program.cs's 0.0001 and here in degrees, 11 m: it moved the
            // point of a zone 0.4 m wide - the two halves of a lake triangle 7.5 m long between
            // Michigan and Muskigumee - off the zone and into Michigan.
            Point point = new Point(InteriorPoint.GetInteriorCoord(shape.Polygon));
            foreach ((Shape other, Country owner) in index.Query(point.EnvelopeInternal))
            {
                if (!prepared.TryGetValue(other, out IPreparedGeometry? geometry))
                {
                    geometry = PreparedGeometryFactory.Prepare(other.Polygon);
                    prepared.Add(other, geometry);
                }

                if (geometry.Contains(point))
                {
                    // How much of it lies there, which tells a shape drawn over another, at all of
                    // it, from one that only touches. Measured and not used to repair anything.
                    double shared = shape.Polygon.Intersection(other.Polygon).Area / shape.Polygon.Area;
                    yield return string.Create(
                        CultureInfo.InvariantCulture,
                        $"{kind} {shape.Id} of {country.Id} at {point.X:F6}, {point.Y:F6} in {owner.Id}'s {other.Id}, {shared:P1} of it");
                }
            }
        }
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
                "{Unpaired} of the {UsedOnce} segments used once lie on no line that may have one side to it - a "
                + "coast, a lake shore, the rim, an outer limit or the edge of a face left out - which is "
                + "{UnpairedShare:0.000%} of the boundary. Borders are not being shared.",
                report.Unpaired,
                report.UsedOnce,
                report.UnpairedShare);

            return;
        }

        Log.Information(
            "Of the {Segments} segments, {Shared} are shared exactly and {OneSided} have one side to them, of "
            + "which {Unpaired} ({UnpairedShare:0.000%} of the boundary) lie on no line that may have one side to "
            + "it and remain unaccounted for.",
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
