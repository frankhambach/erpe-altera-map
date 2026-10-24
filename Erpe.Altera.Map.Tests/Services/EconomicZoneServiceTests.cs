// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System;
using System.Linq;

using Erpe.Altera.Map.Models;
using Erpe.Altera.Map.Services;
using Erpe.Testing;

using FluentAssertions;

using NetTopologySuite.Geometries;

using NUnit.Framework;

[TestFixture]
[TestOf(typeof(EconomicZoneService))]
public class EconomicZoneServiceTests
{
    private const double EarthRadius = 6371.0;

    private const double ZoneRadius = 370.4;

    // A map of roughly the source's proportions, so that a unit of it is a few kilometres.
    private static readonly Envelope SourceEnvelope = new Envelope(0.0, 6000.0, 0.0, 3700.0);

    [AutoCustomizedData]
    [Test]
    public void GetBoundaries_ShouldDrawTheMedianLineHalfwayAcross_WhenTwoCountriesFaceEachOtherOverAStrait(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Two straight shores a degree of longitude apart on the equator, 111 km, each running
        // two degrees north to south. Away from their ends the line between them is the meridian
        // halfway across, and the sampling costs it no more than a few metres.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Country west = new Country("West", CountryType.UnitaryState);
        Country east = new Country("East", CountryType.UnitaryState);
        Shore[] shores =
        [
            CreateShore(arcGraph, west, [(0.0, -1.0), (0.0, 1.0)]),
            CreateShore(arcGraph, east, [(1.0, 1.0), (1.0, -1.0)]),
        ];

        ZoneBoundary[] medians =
        [
            .. CreateService().GetBoundaries(shores, SourceEnvelope).Where(boundary => boundary.Kind == ArcKind.Median),
        ];

        medians.Should().ContainSingle();
        Coordinate[] middle = [.. medians[0].Coordinates.Where(coordinate => Math.Abs(coordinate.Y) < 0.5)];
        middle.Should().NotBeEmpty();
        middle.Should().OnlyContain(coordinate => Math.Abs(coordinate.X - 0.5) < 1e-4);
    }

    [AutoCustomizedData]
    [Test]
    public void GetBoundaries_ShouldCloseALimitRoundAnIsland_WhenNothingElseIsWithinReach(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // An islet a couple of kilometres across, alone in the ocean: its zone is a disc, and the
        // limit is one loop round it at 200 nautical miles from its shore.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Country island = new Country("Island", CountryType.UnitaryState);
        Shore shore = CreateShore(
            arcGraph,
            island,
            [(30.0, 10.0), (30.01, 10.0), (30.01, 10.01), (30.0, 10.01), (30.0, 10.0)]);

        ZoneBoundary[] boundaries = [.. CreateService().GetBoundaries([shore], SourceEnvelope)];

        boundaries.Should().ContainSingle().Which.Kind.Should().Be(ArcKind.Limit);
        Coordinate[] limit = [.. boundaries[0].Coordinates];
        limit[0].Equals2D(limit[^1]).Should().BeTrue("the limit closes on itself round the island");
        limit.Should()
            .OnlyContain(
                coordinate => Math.Abs(GetDistance(coordinate, new Coordinate(30.005, 10.005)) - ZoneRadius) < 2.0);
    }

    [AutoCustomizedData]
    [Test]
    public void GetBoundaries_ShouldDrawNoMedianLine_WhenEveryShoreIsOneCountry(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The same strait with the same country on both sides of it, which is a bay and not a
        // border: the Voronoi edges between its samples separate nothing.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Country country = new Country("Bay", CountryType.UnitaryState);
        Shore[] shores =
        [
            CreateShore(arcGraph, country, [(0.0, -1.0), (0.0, 1.0)]),
            CreateShore(arcGraph, country, [(1.0, 1.0), (1.0, -1.0)]),
        ];

        CreateService().GetBoundaries(shores, SourceEnvelope)
            .Should()
            .NotContain(boundary => boundary.Kind == ArcKind.Median)
            .And.OnlyContain(boundary => boundary.First == country);
    }

    [Test]
    public void GetBoundaries_ShouldThrowArgumentNullException_WhenTheShoresAreNull()
    {
        Action action = () => CreateService().GetBoundaries(null!, SourceEnvelope);

        action.Should().Throw<ArgumentNullException>();
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldEndTheMedianLinesAtTheNodesWhereTheBorderMeetsTheCoast(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // An oblong of land 100 units by 40 at the middle of the map, cut in two by a border down
        // its middle, so that the two countries' coasts meet at its top and at its bottom. The
        // median line between them leaves the land at those two nodes and nowhere else, and it ends
        // at the very nodes of the land graph rather than at a copy of them.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Country west = new Country("West", CountryType.UnitaryState);
        Country east = new Country("East", CountryType.UnitaryState);
        Node top = arcGraph.AddNode(new Coordinate(3000.0, 1830.0));
        Node bottom = arcGraph.AddNode(new Coordinate(3000.0, 1870.0));
        Shore[] shores =
        [
            CreateCoast(arcGraph, west, top, bottom, [(2950.0, 1830.0), (2950.0, 1870.0)]),
            CreateCoast(arcGraph, east, top, bottom, [(3050.0, 1830.0), (3050.0, 1870.0)]),
        ];
        EconomicZoneService service = CreateService();

        ArcGraph water = service.AddArcs(arcGraph, shores, service.GetBoundaries(shores, SourceEnvelope), SourceEnvelope).Graph;

        Arc[] medians = [.. water.Arcs.Where(arc => arc.Kind == ArcKind.Median)];
        medians.Should().HaveCount(2);
        medians.Should().ContainSingle(arc => (arc.StartNode == top) || (arc.EndNode == top));
        medians.Should().ContainSingle(arc => (arc.StartNode == bottom) || (arc.EndNode == bottom));
        water.Arcs.SelectMany(arc => arc.Coordinates.ToCoordinateArray())
            .Should()
            .NotContain(
                coordinate => (coordinate.X > 2950.0) && (coordinate.X < 3050.0) && (coordinate.Y > 1830.0)
                    && (coordinate.Y < 1870.0),
                "nothing of a zone lies over land");
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldCutTheLimitAtTheAntimeridian_WhenItRunsRoundIt(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // An islet just inside the eastern rim, whose limit runs round past it and back. Drawn on
        // the map it would cross from one edge to the other; cut, it stops at either edge.
        WinkelTripelProjectionService projectionService = new WinkelTripelProjectionService(new GeometryFactory());
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Country island = new Country("Island", CountryType.UnitaryState);
        Coordinate[] geographic =
        [
            new Coordinate(179.9, 0.0),
            new Coordinate(179.91, 0.0),
            new Coordinate(179.91, 0.01),
            new Coordinate(179.9, 0.01),
            new Coordinate(179.9, 0.0),
        ];
        Arc arc = arcGraph.AddArc(
            ArcKind.Coast,
            coordinateSequenceFactory.Create(
                [
                    .. geographic.Select(
                        coordinate => projectionService.Unscale(projectionService.Project(coordinate), SourceEnvelope)),
                ]));
        Shore[] shores = [new Shore(island, arc, geographic, 0.0, false)];
        EconomicZoneService service = new EconomicZoneService(projectionService);

        ArcGraph water = service.AddArcs(arcGraph, shores, service.GetBoundaries(shores, SourceEnvelope), SourceEnvelope).Graph;

        water.Arcs.Should().HaveCountGreaterThan(1, "the limit is cut where it crosses the antimeridian");
        foreach (Arc limit in water.Arcs)
        {
            Coordinate[] coordinates = limit.GetFullCoordinates();
            coordinates.Zip(coordinates.Skip(1))
                .Should()
                .OnlyContain(pair => Math.Abs(pair.First.X - pair.Second.X) < 100.0, "no segment runs across the map");
        }
    }

    [Test]
    public void AddArcs_ShouldThrowArgumentNullException_WhenTheGraphIsNull()
    {
        Action action = () => CreateService().AddArcs(null!, [], [], SourceEnvelope);

        action.Should().Throw<ArgumentNullException>();
    }

    private static EconomicZoneService CreateService()
    {
        return new EconomicZoneService(new WinkelTripelProjectionService(new GeometryFactory()));
    }

    // A coast arc between two nodes, drawn in the source map's coordinates for the land graph and
    // carried in longitude and latitude for the zones, as ConvertCommand hands it over.
    private static Shore CreateCoast(
        ArcGraph arcGraph,
        Country owner,
        Node start,
        Node end,
        (double X, double Y)[] interior)
    {
        WinkelTripelProjectionService projectionService = new WinkelTripelProjectionService(new GeometryFactory());
        Arc arc = arcGraph.AddArc(
            ArcKind.Coast,
            start,
            end,
            arcGraph.CoordinateSequenceFactory.Create([.. interior.Select(point => new Coordinate(point.X, point.Y))]));

        return new Shore(
            owner,
            arc,
            [
                .. arc.GetFullCoordinates()
                    .Select(
                        coordinate => projectionService.Unproject(projectionService.Rescale(coordinate, SourceEnvelope))),
            ],
            0.0,
            false);
    }

    // A shore given in longitude and latitude, drawn into the source map as ConvertCommand would
    // find it there.
    private static Shore CreateShore(ArcGraph arcGraph, Country owner, (double X, double Y)[] points)
    {
        WinkelTripelProjectionService projectionService = new WinkelTripelProjectionService(new GeometryFactory());
        Coordinate[] coordinates = [.. points.Select(point => new Coordinate(point.X, point.Y))];
        Arc arc = arcGraph.AddArc(
            ArcKind.Coast,
            arcGraph.CoordinateSequenceFactory.Create(
                [
                    .. coordinates.Select(
                        coordinate => projectionService.Unscale(projectionService.Project(coordinate), SourceEnvelope)),
                ]));

        return new Shore(owner, arc, coordinates, 0.0, false);
    }

    private static double GetDistance(Coordinate one, Coordinate other)
    {
        return SphereVector.FromGeographic(one).AngleTo(SphereVector.FromGeographic(other)) * EarthRadius;
    }
}
