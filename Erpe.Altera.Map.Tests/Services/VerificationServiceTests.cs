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
[TestOf(typeof(VerificationService))]
public class VerificationServiceTests
{
    [AutoCustomizedData]
    [Test]
    public void VerifySharing_ShouldCountTheBorderOnce_WhenTwoCountriesShareIt(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        Country west = CreateCountry(geometryFactory, "West", 0, 0, 10);
        Country east = CreateCountry(geometryFactory, "East", 10, 0, 10);

        SharingReport report = new VerificationService().VerifySharing(
            [west, east],
            new ArcGraph(coordinateSequenceFactory),
            false);

        report.Shared.Should().Be(1, "the two squares meet along one edge, walked both ways");
        report.UsedOnce.Should().Be(6, "the three outer edges of each square");
        report.SameDirection.Should().Be(0);
        report.Excess.Should().Be(0);
    }

    [AutoCustomizedData]
    [Test]
    public void VerifySharing_ShouldCountNothingAsShared_WhenTheTwoSidesCarryDifferentCoordinates(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The failure the check exists for. The two countries meet along the same line, but
        // one of them carries a vertex partway along it that the other does not, so neither
        // side writes the same pair of coordinates as the other.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        Country west = CreateCountry(geometryFactory, "West", 0, 0, 10);
        Country east = new Country(
            "East",
            CountryType.UnitaryState,
            [
                new Shape(
                    "East-0",
                    geometryFactory.CreatePolygon(
                    [
                        new Coordinate(10, 0),
                        new Coordinate(20, 0),
                        new Coordinate(20, 10),
                        new Coordinate(10, 10),
                        new Coordinate(10, 4),
                        new Coordinate(10, 0),
                    ])),
            ]);

        SharingReport report = new VerificationService().VerifySharing(
            [west, east],
            new ArcGraph(coordinateSequenceFactory),
            false);

        report.Shared.Should().Be(0, "one side has split the border in two and the other has not");
        report.UsedOnce.Should().Be(9);
    }

    [AutoCustomizedData]
    [Test]
    public void VerifySharing_ShouldCountTheExcess_WhenTwoCountriesAreDrawnOverEachOther(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Which the sample map really does contain: four pairs of countries drawn with the
        // same shape, so the border between them was never drawn at all.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        Country west = CreateCountry(geometryFactory, "West", 0, 0, 10);
        Country east = CreateCountry(geometryFactory, "East", 10, 0, 10);
        Country twin = CreateCountry(geometryFactory, "Twin", 10, 0, 10);

        SharingReport report = new VerificationService().VerifySharing(
            [west, east, twin],
            new ArcGraph(coordinateSequenceFactory),
            false);

        report.Excess.Should().Be(1, "three rings run along the border between west and the twins");
        report.Shared.Should().Be(0);
        report.SameDirection
            .Should()
            .Be(3, "the twins walk their three other edges the same way, which is what lying on top looks like");
    }

    [AutoCustomizedData]
    [Test]
    public void VerifySharing_ShouldMeasureTheShortfall_WhenTheLayerCoversTheLand(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The only part of the check with teeth: what is left over has to be the coast, and
        // the coast is counted from the graph rather than from the output.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        arcGraph.AddArc(
            ArcKind.Coast,
            coordinateSequenceFactory.Create(
            [
                new Coordinate(0, 0),
                new Coordinate(20, 0),
                new Coordinate(20, 10),
                new Coordinate(0, 10),
                new Coordinate(0, 0),
            ]));
        Country west = CreateCountry(geometryFactory, "West", 0, 0, 10);
        Country east = CreateCountry(geometryFactory, "East", 10, 0, 10);

        SharingReport report = new VerificationService().VerifySharing([west, east], arcGraph, true);

        report.CoastSegments.Should().Be(4, "the one coast ring has four segments");
        report.UsedOnce.Should().Be(6, "the coast of each square, which is two segments more than the ring");
        report.Shortfall.Should().BeApproximately(-0.5, 1E-9);
    }

    [AutoCustomizedData]
    [Test]
    public void VerifySharing_ShouldThrowArgumentNullException_WhenCountriesAreNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => new VerificationService().VerifySharing(
            null!,
            new ArcGraph(coordinateSequenceFactory),
            false);

        action.Should().Throw<ArgumentNullException>();
    }

    private static Coordinate[] CreateRing(double x, double y, double width, double height)
    {
        return
        [
            new Coordinate(x, y),
            new Coordinate(x + width, y),
            new Coordinate(x + width, y + height),
            new Coordinate(x, y + height),
            new Coordinate(x, y),
        ];
    }

    private static Country CreateCountry(GeometryFactory geometryFactory, string id, double x, double y, double size)
    {
        return new Country(
            id,
            CountryType.UnitaryState,
            [new Shape(id + "-0", geometryFactory.CreatePolygon(CreateRing(x, y, size, size)))]);
    }

    private static GeometryFactory CreateGeometryFactory(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new GeometryFactory(new PrecisionModel(10000.0), 0, coordinateSequenceFactory);
    }
}
