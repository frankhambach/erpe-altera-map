// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Erpe.Altera.Map.Models;
using Erpe.Altera.Map.Services;
using Erpe.Testing;

using FluentAssertions;

using NetTopologySuite.Geometries;

using NUnit.Framework;

[TestFixture]
[TestOf(typeof(AssemblyService))]
public class AssemblyServiceTests
{
    [AutoCustomizedData]
    [Test]
    public void Assemble_ShouldDissolveTheFacesIntoOneShape_WhenOneCountryOwnsBothSidesOfABorder(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        (ArcGraph arcGraph, Arc border) = CreateDividedSquare(geometryFactory);
        Country country = new Country("A", CountryType.UnitaryState);
        SetOwner(arcGraph.Faces, country);

        IReadOnlyList<Face> dropped = CreateService(geometryFactory).Assemble(arcGraph, [country]);

        dropped.Should().BeEmpty();
        Shape shape = country.Shapes.Should().ContainSingle().Subject;
        shape.Id.Should().Be("A-0");
        shape.Polygon.Area.Should().BeApproximately(200.0, 1E-9, "both halves together");
        shape.Rings.Should()
            .ContainSingle()
            .Which.Should()
            .NotContain(directed => directed.Arc == border, "a border with the country on both sides drops out");
    }

    [AutoCustomizedData]
    [Test]
    public void Assemble_ShouldBuildBothSidesFromTheSameArc_WhenTwoCountriesShareABorder(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        (ArcGraph arcGraph, Arc border) = CreateDividedSquare(geometryFactory);
        Country north = new Country("North", CountryType.UnitaryState);
        Country south = new Country("South", CountryType.UnitaryState);
        arcGraph.Faces.Should().HaveCount(2);
        arcGraph.Faces[0].Owner = north;
        arcGraph.Faces[1].Owner = south;

        CreateService(geometryFactory).Assemble(arcGraph, [north, south]);

        Shape[] shapes = [north.Shapes.Should().ContainSingle().Subject, south.Shapes.Should().ContainSingle().Subject];
        shapes.Should().OnlyContain(shape => Math.Abs(shape.Polygon.Area - 100.0) < 1E-9);
        shapes.Select(shape => shape.Rings[0].Single(directed => directed.Arc == border).IsReversed)
            .Should()
            .BeEquivalentTo([true, false], "each side walks the one border arc, the opposite way from the other");
    }

    [AutoCustomizedData]
    [Test]
    public void Assemble_ShouldCarryTheLakeAsAHole_WhenTheCountryEnclosesAFaceNobodyOwns(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddIslandWithALake(arcGraph, coordinateSequenceFactory, 0.0);
        new TopologyService(geometryFactory).BuildFaces(arcGraph, 0.0, 0.0);
        Country country = new Country("A", CountryType.UnitaryState);
        arcGraph.Faces.Single(face => face.Holes.Length == 1).Owner = country;

        CreateService(geometryFactory).Assemble(arcGraph, [country]);

        Shape shape = country.Shapes.Should().ContainSingle().Subject;
        shape.Polygon.Holes.Should().ContainSingle();
        shape.Polygon.Area.Should().BeApproximately(800.0, 1E-9, "the island less its lake");
        shape.Rings.Should().HaveCount(2, "the shell's arcs and the hole's, one for one with the polygon's rings");
    }

    [AutoCustomizedData]
    [Test]
    public void Assemble_ShouldGiveEachHoleToTheShellAroundIt_WhenACountryHasSeveralIslandsWithLakes(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // With one shell every hole is its own; with two, each hole has to be put in the shell
        // that encloses it.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddIslandWithALake(arcGraph, coordinateSequenceFactory, 0.0);
        AddIslandWithALake(arcGraph, coordinateSequenceFactory, 100.0);
        new TopologyService(geometryFactory).BuildFaces(arcGraph, 0.0, 0.0);
        Country country = new Country("A", CountryType.UnitaryState);
        SetOwner(arcGraph.Faces.Where(face => face.Holes.Length == 1), country);

        CreateService(geometryFactory).Assemble(arcGraph, [country]);

        country.Shapes.Should().HaveCount(2);
        country.Shapes.Should()
            .OnlyContain(
                shape => (shape.Polygon.Holes.Length == 1)
                    && shape.Polygon.Shell.EnvelopeInternal.Contains(shape.Polygon.Holes[0].EnvelopeInternal));
    }

    [AutoCustomizedData]
    [Test]
    public void Assemble_ShouldMakeTwoValidShapes_WhenTwoFacesOfOneCountryTouchAtAPoint(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // As one ring through the node twice this would touch itself, which NetTopologySuite
        // rejects and which would cost the country both faces. It has to come out as two.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Node pinch = arcGraph.AddNode(new Coordinate(10.0, 10.0));
        arcGraph.AddArc(ArcKind.Coast, pinch, pinch, Create(coordinateSequenceFactory, [(0, 10), (0, 0), (10, 0)]));
        arcGraph.AddArc(
            ArcKind.Coast,
            pinch,
            pinch,
            Create(coordinateSequenceFactory, [(20, 10), (20, 20), (10, 20)]));
        new TopologyService(geometryFactory).BuildFaces(arcGraph, 0.0, 0.0);
        Country country = new Country("A", CountryType.UnitaryState);
        SetOwner(arcGraph.Faces, country);

        IReadOnlyList<Face> dropped = CreateService(geometryFactory).Assemble(arcGraph, [country]);

        dropped.Should().BeEmpty();
        country.Shapes.Should().HaveCount(2);
        country.Shapes.Should().OnlyContain(shape => shape.Polygon.IsValid && (Math.Abs(shape.Polygon.Area - 100.0) < 1E-9));
    }

    [AutoCustomizedData]
    [Test]
    public void Assemble_ShouldLeaveTheCountryWithoutShapes_WhenItOwnsNoFace(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The drawn shapes are replaced by what the faces make, and no face makes nothing.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        (ArcGraph arcGraph, _) = CreateDividedSquare(geometryFactory);
        Country owner = new Country("A", CountryType.UnitaryState);
        Country drawn = new Country(
            "B",
            CountryType.UnitaryState,
            [new Shape("B-0", (Polygon)geometryFactory.ToGeometry(new Envelope(0, 1, 0, 1)))]);
        SetOwner(arcGraph.Faces, owner);

        CreateService(geometryFactory).Assemble(arcGraph, [owner, drawn]);

        owner.Shapes.Should().ContainSingle();
        drawn.Shapes.Should().BeEmpty();
    }

    [AutoCustomizedData]
    [Test]
    public void Assemble_ShouldThrowArgumentNullException_WhenArcGraphIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => CreateService(CreateGeometryFactory(coordinateSequenceFactory)).Assemble(null!, []);

        action.Should().Throw<ArgumentNullException>();
    }

    [AutoCustomizedData]
    [Test]
    public void Assemble_ShouldThrowArgumentNullException_WhenCountriesAreNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        Action action = () => CreateService(CreateGeometryFactory(coordinateSequenceFactory)).Assemble(arcGraph, null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [AutoCustomizedData]
    [Test]
    public void Constructor_ShouldThrowArgumentNullException_WhenTopologyServiceIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => _ = new AssemblyService(CreateGeometryFactory(coordinateSequenceFactory), null!);

        action.Should().Throw<ArgumentNullException>();
    }

    // A square ten units either side of a border along y = 0, traced into its two faces.
    private static (ArcGraph ArcGraph, Arc Border) CreateDividedSquare(GeometryFactory geometryFactory)
    {
        CoordinateSequenceFactory coordinateSequenceFactory = geometryFactory.CoordinateSequenceFactory;
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Node west = arcGraph.AddNode(new Coordinate(0, 0));
        Node east = arcGraph.AddNode(new Coordinate(10, 0));
        arcGraph.AddArc(ArcKind.Coast, west, east, Create(coordinateSequenceFactory, [(0, 10), (10, 10)]));
        arcGraph.AddArc(ArcKind.Coast, west, east, Create(coordinateSequenceFactory, [(0, -10), (10, -10)]));
        Arc border = arcGraph.AddArc(ArcKind.Border, west, east, Create(coordinateSequenceFactory, []));
        new TopologyService(geometryFactory).BuildFaces(arcGraph, 0.0, 0.0);

        return (arcGraph, border);
    }

    // An island 30 units square at x, 0 with a lake 10 units square in its middle.
    private static void AddIslandWithALake(
        ArcGraph arcGraph,
        CoordinateSequenceFactory coordinateSequenceFactory,
        double x)
    {
        arcGraph.AddArc(
            ArcKind.Coast,
            Create(coordinateSequenceFactory, [(x, 0), (x + 30, 0), (x + 30, 30), (x, 30), (x, 0)]));
        arcGraph.AddArc(
            ArcKind.Lake,
            Create(coordinateSequenceFactory, [(x + 10, 10), (x + 20, 10), (x + 20, 20), (x + 10, 20), (x + 10, 10)]));
    }

    private static CoordinateSequence Create(
        CoordinateSequenceFactory coordinateSequenceFactory,
        (double X, double Y)[] points)
    {
        return coordinateSequenceFactory.Create(
            points.Select(point => new Coordinate(point.X, point.Y)).ToArray());
    }

    private static GeometryFactory CreateGeometryFactory(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new GeometryFactory(new PrecisionModel(10000.0), 0, coordinateSequenceFactory);
    }

    private static AssemblyService CreateService(GeometryFactory geometryFactory)
    {
        return new AssemblyService(geometryFactory, new TopologyService(geometryFactory));
    }

    private static void SetOwner(IEnumerable<Face> faces, Country owner)
    {
        foreach (Face face in faces)
        {
            face.Owner = owner;
        }
    }
}
