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
            .. new EconomicZoneService().GetBoundaries(shores).Where(boundary => boundary.Kind == ArcKind.Median),
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

        ZoneBoundary[] boundaries = [.. new EconomicZoneService().GetBoundaries([shore])];

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

        new EconomicZoneService().GetBoundaries(shores)
            .Should()
            .NotContain(boundary => boundary.Kind == ArcKind.Median)
            .And.OnlyContain(boundary => boundary.First == country);
    }

    [Test]
    public void GetBoundaries_ShouldThrowArgumentNullException_WhenTheShoresAreNull()
    {
        Action action = () => new EconomicZoneService().GetBoundaries(null!);

        action.Should().Throw<ArgumentNullException>();
    }

    private static Shore CreateShore(ArcGraph arcGraph, Country owner, (double X, double Y)[] points)
    {
        Coordinate[] coordinates = [.. points.Select(point => new Coordinate(point.X, point.Y))];
        Arc arc = arcGraph.AddArc(ArcKind.Coast, arcGraph.CoordinateSequenceFactory.Create(coordinates));

        return new Shore(owner, arc, coordinates);
    }

    private static double GetDistance(Coordinate one, Coordinate other)
    {
        return SphereVector.FromGeographic(one).AngleTo(SphereVector.FromGeographic(other)) * EarthRadius;
    }
}
