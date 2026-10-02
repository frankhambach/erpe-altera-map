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
[TestOf(typeof(FaceAssignmentService))]
public class FaceAssignmentServiceTests
{
    [AutoCustomizedData]
    [Test]
    public void Assign_ShouldGiveTheFaceToTheCountry_WhenOneShapeCoversIt(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddRing(arcGraph, coordinateSequenceFactory, 0, 0, 30);
        new TopologyService(geometryFactory).BuildFaces(arcGraph);
        Country country = CreateCountry(geometryFactory, "Nowhere", 0, 0, 30);

        new FaceAssignmentService(geometryFactory).Assign(arcGraph, [country]);

        arcGraph.Faces.Should().ContainSingle().Which.Owner.Should().BeSameAs(country);
    }

    [AutoCustomizedData]
    [Test]
    public void Assign_ShouldLeaveTheLakeUnowned_WhenItLiesInsideLand(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The lake is water, not territory, and the country shape covers it because the
        // shapes were drawn solid. Nesting, not the shape, is what settles it.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddRing(arcGraph, coordinateSequenceFactory, 0, 0, 30);
        AddRing(arcGraph, coordinateSequenceFactory, 10, 10, 10);
        new TopologyService(geometryFactory).BuildFaces(arcGraph);
        Country country = CreateCountry(geometryFactory, "Nowhere", 0, 0, 30);

        new FaceAssignmentService(geometryFactory).Assign(arcGraph, [country]);

        arcGraph.Faces.Should().HaveCount(2);
        arcGraph.Faces.Count(face => face.Owner == null).Should().Be(1, "the lake stays water");
        arcGraph.Faces.Count(face => face.Owner == country).Should().Be(1, "the land around it does not");
    }

    [AutoCustomizedData]
    [Test]
    public void Assign_ShouldOwnTheIsland_WhenItLiesInsideALake(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Land and water alternate with depth, so an island in a lake is land again.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddRing(arcGraph, coordinateSequenceFactory, 0, 0, 60);
        AddRing(arcGraph, coordinateSequenceFactory, 10, 10, 40);
        AddRing(arcGraph, coordinateSequenceFactory, 20, 20, 20);
        new TopologyService(geometryFactory).BuildFaces(arcGraph);
        Country country = CreateCountry(geometryFactory, "Nowhere", 0, 0, 60);

        new FaceAssignmentService(geometryFactory).Assign(arcGraph, [country]);

        arcGraph.Faces.Should().HaveCount(3);
        arcGraph.Faces.Count(face => face.Owner == country).Should().Be(2, "the outer land and the island");
        arcGraph.Faces.Count(face => face.Owner == null).Should().Be(1, "only the lake between them is water");
    }

    [AutoCustomizedData]
    [Test]
    public void Assign_ShouldAbsorbTheFace_WhenNoCountryCoversIt(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // A face no shape covers is a gap. It goes to whichever neighbour it shares the
        // most border with, which is the whole of gap filling in this representation.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(60, 0));
        AddArc(arcGraph, coordinateSequenceFactory, west, east, [(0, 40), (60, 40)]);
        AddArc(arcGraph, coordinateSequenceFactory, west, east, [(0, -40), (60, -40)]);
        AddArc(arcGraph, coordinateSequenceFactory, west, east, []);
        new TopologyService(geometryFactory).BuildFaces(arcGraph);

        // Only the northern half is covered by a country; the southern half is the gap.
        Country country = CreateCountry(geometryFactory, "Nowhere", 0, 0, 60, 40);

        new FaceAssignmentService(geometryFactory).Assign(arcGraph, [country]);

        arcGraph.Faces.Should().HaveCount(2);
        arcGraph.Faces.Should().OnlyContain(face => face.Owner == country);
    }

    [AutoCustomizedData]
    [Test]
    public void Assign_ShouldThrowArgumentNullException_WhenArcGraphIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);

        Action action = () => new FaceAssignmentService(geometryFactory).Assign(null!, []);

        action.Should().Throw<ArgumentNullException>();
    }

    private static void AddArc(
        ArcGraph arcGraph,
        CoordinateSequenceFactory coordinateSequenceFactory,
        Node startNode,
        Node endNode,
        (double X, double Y)[] interior)
    {
        arcGraph.AddArc(
            ArcKind.Border,
            startNode,
            endNode,
            coordinateSequenceFactory.Create(
                interior.Select(point => new Coordinate(point.X, point.Y)).ToArray()));
    }

    private static void AddRing(
        ArcGraph arcGraph,
        CoordinateSequenceFactory coordinateSequenceFactory,
        double x,
        double y,
        double size)
    {
        arcGraph.AddArc(ArcKind.Coast, coordinateSequenceFactory.Create(CreateRing(x, y, size, size)));
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

    private static Country CreateCountry(
        GeometryFactory geometryFactory,
        string id,
        double x,
        double y,
        double width,
        double? height = null)
    {
        return new Country(
            id,
            CountryType.UnitaryState,
            [
                new Shape(
                    id + "-0",
                    geometryFactory.CreatePolygon(CreateRing(x, y, width, height ?? width))),
            ]);
    }

    private static GeometryFactory CreateGeometryFactory(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new GeometryFactory(new PrecisionModel(10000.0), 0, coordinateSequenceFactory);
    }
}
