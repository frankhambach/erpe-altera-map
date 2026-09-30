// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System.Collections.Generic;

using Erpe.Altera.Map.Services;
using Erpe.Altera.Map.Tests.Testing;
using Erpe.Testing;

using FluentAssertions;
using FluentAssertions.Equivalency;

using NetTopologySuite.Geometries;

using NSubstitute;

using NUnit.Framework;

[TestFixture]
[TestOf(typeof(TopologyService))]
public class TopologyServiceTests
{
    [AutoCustomizedData]
    [Test]
    public void Close_ShouldReturnSameLinearRing_WhenLinearRingIsAlreadyClosed(GeometryFactory geometryFactory)
    {
        CoordinateSequence? coordinateSequence =
            geometryFactory.CoordinateSequenceFactory.Create(
                    [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(0, 0)]);
        TopologyService service = new TopologyService(geometryFactory);

        LinearRing result = service.Close(coordinateSequence);

        result.Coordinates.Should()
            .BeEquivalentTo(
                [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(0, 0)],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void Close_ShouldReturnClosedLinearRing_WhenCoordinateSequenceIsNotClosed(GeometryFactory geometryFactory)
    {
        CoordinateSequence? coordinateSequence =
            geometryFactory.CoordinateSequenceFactory.Create([new Coordinate(0, 0), new Coordinate(1, 1)]);
        TopologyService service = new TopologyService(geometryFactory);

        LinearRing result = service.Close(coordinateSequence);

        result.Coordinates.Should()
            .BeEquivalentTo(
                [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(0, 0)],
                options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void Polygonize_ReturnsPolygonWithHoles_WhenOneLinearRingsEnclosesAnotherLinearRing(GeometryFactory geometryFactory)
    {
        TopologyService service = new TopologyService(geometryFactory);
        LinearRing shell = geometryFactory.CreateLinearRing(
            [
                new Coordinate(0, 0), new Coordinate(3, 0), new Coordinate(3, 3), new Coordinate(0, 3),
                new Coordinate(0, 0),
            ]);
        LinearRing hole = geometryFactory.CreateLinearRing(
            [
                new Coordinate(1, 1), new Coordinate(2, 1), new Coordinate(2, 2), new Coordinate(1, 2),
                new Coordinate(1, 1),
            ]);

        IEnumerable<Polygon> result = service.Polygonize([shell, hole]);

        result.Should().BeEquivalentTo([geometryFactory.CreatePolygon(shell, [hole])]);
    }

    [AutoCustomizedData]
    [Test]
    public void Polygonize_ReturnsPolygonWithMultipleHoles_WhenOneLinearRingEnclosesMultipleLinearRings(GeometryFactory geometryFactory)
    {
        TopologyService service = new TopologyService(geometryFactory);
        LinearRing shell = geometryFactory.CreateLinearRing(
            [
                new Coordinate(0, 0), new Coordinate(3, 0), new Coordinate(3, 5), new Coordinate(0, 5),
                new Coordinate(0, 0),
            ]);
        LinearRing firstHole = geometryFactory.CreateLinearRing(
            [
                new Coordinate(1, 1), new Coordinate(2, 1), new Coordinate(2, 2), new Coordinate(1, 2),
                new Coordinate(1, 1),
            ]);
        LinearRing secondHole = geometryFactory.CreateLinearRing(
            [
                new Coordinate(1, 3), new Coordinate(2, 3), new Coordinate(2, 4), new Coordinate(1, 4),
                new Coordinate(1, 3),
            ]);

        IEnumerable<Polygon> result = service.Polygonize([firstHole, secondHole, shell]);

        result.Should().BeEquivalentTo([geometryFactory.CreatePolygon(shell, [firstHole, secondHole])]);
    }

    [AutoCustomizedData]
    [Test]
    public void Polygonize_ReturnsNestedPolygonWithHoles_WhenMultipleLinearRingsAreNested(GeometryFactory geometryFactory)
    {
        TopologyService service = new TopologyService(geometryFactory);
        LinearRing outerShell = geometryFactory.CreateLinearRing(
            [
                new Coordinate(0, 0), new Coordinate(7, 0), new Coordinate(7, 7), new Coordinate(0, 7),
                new Coordinate(0, 0),
            ]);
        LinearRing outerHole = geometryFactory.CreateLinearRing(
            [
                new Coordinate(1, 1), new Coordinate(6, 1), new Coordinate(6, 6), new Coordinate(1, 6),
                new Coordinate(1, 1),
            ]);
        LinearRing innerShell = geometryFactory.CreateLinearRing(
            [
                new Coordinate(2, 2), new Coordinate(5, 2), new Coordinate(5, 5), new Coordinate(2, 5),
                new Coordinate(2, 2),
            ]);
        LinearRing innerHole = geometryFactory.CreateLinearRing(
            [
                new Coordinate(3, 3), new Coordinate(4, 3), new Coordinate(4, 4), new Coordinate(3, 4),
                new Coordinate(3, 3),
            ]);

        IEnumerable<Polygon> result = service.Polygonize([innerHole, outerShell, outerHole, innerShell]);

        result.Should()
            .BeEquivalentTo(
                [
                    geometryFactory.CreatePolygon(outerShell, [outerHole]),
                    geometryFactory.CreatePolygon(innerShell, [innerHole]),
                ]);
    }

    [AutoCustomizedData]
    [Test]
    public void Polygonize_ReturnsSeparatePolygonsWithHoles_WhenSeparateLinearRingsEncloseLinearRings(GeometryFactory geometryFactory)
    {
        TopologyService service = new TopologyService(geometryFactory);
        LinearRing firstShell = geometryFactory.CreateLinearRing(
            [
                new Coordinate(0, 0), new Coordinate(3, 0), new Coordinate(3, 3), new Coordinate(0, 3),
                new Coordinate(0, 0),
            ]);
        LinearRing firstHole = geometryFactory.CreateLinearRing(
            [
                new Coordinate(1, 1), new Coordinate(2, 1), new Coordinate(2, 2), new Coordinate(1, 2),
                new Coordinate(1, 1),
            ]);
        LinearRing secondShell = geometryFactory.CreateLinearRing(
            [
                new Coordinate(4, 0), new Coordinate(7, 0), new Coordinate(7, 3), new Coordinate(4, 3),
                new Coordinate(4, 0),
            ]);
        LinearRing secondHole = geometryFactory.CreateLinearRing(
            [
                new Coordinate(5, 1), new Coordinate(6, 1), new Coordinate(6, 2), new Coordinate(5, 2),
                new Coordinate(5, 1),
            ]);

        IEnumerable<Polygon> result = service.Polygonize([firstShell, secondHole, firstHole, secondShell]);

        result.Should()
            .BeEquivalentTo(
                [
                    geometryFactory.CreatePolygon(firstShell, [firstHole]),
                    geometryFactory.CreatePolygon(secondShell, [secondHole]),
                ]);
    }

    [AutoCustomizedData]
    [Test]
    public void Unloop_ShouldReturnSameLinearRing_WhenLinearRingIsNotSelfIntersecting(GeometryFactory geometryFactory)
    {
        TopologyService service = new TopologyService(geometryFactory);

        LinearRing result = service.Unloop(
            geometryFactory.CreateLinearRing(
                [
                    new Coordinate(0, 0), new Coordinate(3, 0), new Coordinate(2, 1), new Coordinate(0, 0),
                ]));

        result.Should()
            .BeTopologicallyEqualTo(
                geometryFactory.CreateLinearRing(
                    [
                        new Coordinate(0, 0), new Coordinate(3, 0), new Coordinate(2, 1), new Coordinate(0, 0),
                    ]));
    }

    [AutoCustomizedData]
    [Test]
    public void Unloop_ShouldReturnLargestLoop_WhenSelfIntersectingLinearRingIsProvided(GeometryFactory geometryFactory)
    {
        TopologyService service = new TopologyService(geometryFactory);

        LinearRing result = service.Unloop(
            geometryFactory.CreateLinearRing(
                [
                    new Coordinate(0, 0), new Coordinate(3, 0), new Coordinate(2, 1), new Coordinate(2, -1),
                    new Coordinate(0, 0),
                ]));

        result.Should()
            .BeTopologicallyEqualTo(
                geometryFactory.CreateLinearRing(
                    [
                        new Coordinate(0, 0), new Coordinate(2, 0), new Coordinate(2, -1),
                        new Coordinate(0, 0),
                    ]));
    }

    [AutoCustomizedData]
    [Test]
    public void Unloop_ShouldReturnLargestLoop_WhenLinearRingWithMultipleSelfIntersectionsIsProvided(
        GeometryFactory geometryFactory)
    {
        TopologyService service = new TopologyService(geometryFactory);

        LinearRing result = service.Unloop(
            geometryFactory.CreateLinearRing(
                [
                    new Coordinate(-1, 0), new Coordinate(3, 0), new Coordinate(2, 1), new Coordinate(2, -1),
                    new Coordinate(-1, 0.5), new Coordinate(-1, 0),
                ]));

        result.Should()
            .BeTopologicallyEqualTo(
                geometryFactory.CreateLinearRing(
                    [
                        new Coordinate(0, 0), new Coordinate(2, 0), new Coordinate(2, -1),
                        new Coordinate(0, 0),
                    ]));
    }
}