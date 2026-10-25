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

        SharingReport report = new VerificationService().VerifySharing([west, east], null);

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

        SharingReport report = new VerificationService().VerifySharing([west, east], null);

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

        SharingReport report = new VerificationService().VerifySharing([west, east, twin], null);

        report.Excess.Should().Be(1, "three rings run along the border between west and the twins");
        report.Shared.Should().Be(0);
        report.SameDirection
            .Should()
            .Be(3, "the twins walk their three other edges the same way, which is what lying on top looks like");
    }

    [AutoCustomizedData]
    [Test]
    public void VerifySharing_ShouldAccountForEveryOneSidedSegment_WhenTheLayerCoversTheLand(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The only part of the check with teeth: what is left over has to be the coast, and
        // the coast is counted from the graph rather than from the output.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        Country west = CreateCountry(geometryFactory, "West", 0, 0, 10);
        Country east = CreateCountry(geometryFactory, "East", 10, 0, 10);

        Coordinate[][] coast = CreateCoast();

        SharingReport report = new VerificationService().VerifySharing([west, east], coast);

        report.UsedOnce.Should().Be(6, "the outer edges of the two squares");
        report.Unpaired.Should().Be(0, "and every one of them is coast");
    }

    [AutoCustomizedData]
    [Test]
    public void VerifySharing_ShouldCountTheBorderAsUnpaired_WhenOnlyOneSideCarriesIt(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // What a layer looks like when sharing has broken: the border between the two is
        // neither shared nor coast, so it is boundary with one side to it.
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
        Coordinate[][] coast = CreateCoast();

        SharingReport report = new VerificationService().VerifySharing([west, east], coast);

        report.Shared.Should().Be(0);
        report.Unpaired.Should().Be(3, "one side of the border as one segment and the other as two");
    }

    [Test]
    public void VerifySharing_ShouldThrowArgumentNullException_WhenCountriesAreNull()
    {
        Action action = () => new VerificationService().VerifySharing(null!, null);

        action.Should().Throw<ArgumentNullException>();
    }

    [AutoCustomizedData]
    [Test]
    public void VerifyDisjoint_ShouldFindNothing_WhenTheZoneHasAHoleForTheIsland(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        Country island = CreateCountry(geometryFactory, "Island", 10, 10, 10);
        Country zone = CreateZone(geometryFactory, true);

        int inside = new VerificationService().VerifyDisjoint([island], [zone]);

        inside.Should().Be(0);
    }

    [AutoCustomizedData]
    [Test]
    public void VerifyDisjoint_ShouldFindTheIsland_WhenTheZoneHasNoHoleForIt(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The failure the check exists for, and the one sharing cannot see: the island's coast is
        // used once, as a coast should be, while the zone is drawn straight over it.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        Country island = CreateCountry(geometryFactory, "Island", 10, 10, 10);
        Country zone = CreateZone(geometryFactory, false);

        SharingReport report = new VerificationService().VerifySharing([island, zone], null);
        int inside = new VerificationService().VerifyDisjoint([island], [zone]);

        report.Excess.Should().Be(0, "no segment is used more than once");
        inside.Should().Be(2, "the island lies inside the zone, and the zone's middle is the island");
    }

    [Test]
    public void VerifyDisjoint_ShouldThrowArgumentNullException_WhenTheZonesAreNull()
    {
        Action action = () => new VerificationService().VerifyDisjoint([], null!);

        action.Should().Throw<ArgumentNullException>();
    }

    // The coast around a pair of squares that sit side by side: six of their eight edges,
    // split where the border between them reaches the sea, which is what welding leaves.
    private static Coordinate[][] CreateCoast()
    {
        return
        [
            [new Coordinate(0, 0), new Coordinate(10, 0)],
            [new Coordinate(10, 0), new Coordinate(20, 0)],
            [new Coordinate(20, 0), new Coordinate(20, 10)],
            [new Coordinate(20, 10), new Coordinate(10, 10)],
            [new Coordinate(10, 10), new Coordinate(0, 10)],
            [new Coordinate(0, 10), new Coordinate(0, 0)],
        ];
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

    // A zone 30 units square around an island 10 units square in its middle, with a hole for the
    // island or without one.
    private static Country CreateZone(GeometryFactory geometryFactory, bool hasHole)
    {
        LinearRing[] holes = hasHole ? [geometryFactory.CreateLinearRing(CreateRing(10, 10, 10, 10))] : [];

        return new Country(
            "Island",
            CountryType.UnitaryState,
            [
                new Shape(
                    "Island-0",
                    geometryFactory.CreatePolygon(geometryFactory.CreateLinearRing(CreateRing(0, 0, 30, 30)), holes)),
            ]);
    }

    private static GeometryFactory CreateGeometryFactory(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new GeometryFactory(new PrecisionModel(10000.0), 0, coordinateSequenceFactory);
    }
}
