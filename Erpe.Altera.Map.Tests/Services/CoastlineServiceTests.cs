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
[TestOf(typeof(CoastlineService))]
public class CoastlineServiceTests
{
    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldAddOneCoastArc_WhenASinglePolygonIsProvided(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        new CoastlineService(geometryFactory).AddArcs([CreateSquare(geometryFactory, 0, 0, 100)], arcGraph);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.Kind.Should().Be(ArcKind.Coast);
        arc.IsClosed.Should().BeTrue();
        arcGraph.Nodes.Should().ContainSingle();
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldAddOneCoastArc_WhenTwoPolygonsShareAnEdge(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // The shared edge is an internal border, which the tracer layer supplies instead, so
        // unioning has to leave only the outline of the pair.
        new CoastlineService(geometryFactory).AddArcs(
            [CreateSquare(geometryFactory, 0, 0, 50), CreateSquare(geometryFactory, 50, 0, 50)],
            arcGraph);

        arcGraph.Arcs.Should().ContainSingle();
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldKeepHoleAsALakeShore_WhenHoleIsFatEnoughToBeALake(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // A 10 by 10 hole has an area to length ratio of 2.5, far above the gap threshold.
        new CoastlineService(geometryFactory).AddArcs(
            [CreatePolygonWithHole(geometryFactory, 40, 40, 10, 10)],
            arcGraph);

        arcGraph.Arcs.Should().HaveCount(2);

        // The shell is sea coast and the hole is a lake shore. Face assignment later uses
        // that difference to tell water from an enclave.
        arcGraph.Arcs.Select(arc => arc.Kind).Should().BeEquivalentTo([ArcKind.Coast, ArcKind.Lake]);
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldDiscardHole_WhenHoleIsThinEnoughToBeAGap(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // A 20 by 0.1 sliver has an area to length ratio of about 0.05, below the threshold.
        new CoastlineService(geometryFactory).AddArcs(
            [CreatePolygonWithHole(geometryFactory, 40, 40, 20, 0.1)],
            arcGraph);

        arcGraph.Arcs.Should().ContainSingle("the shell is coast but the sliver is a gap");
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldDiscardHole_WhenHoleIsLargeButThin(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // Area 18 is larger than several kept lakes, but the ratio is 0.0989. This is the
        // case a plain area threshold would misclassify, and the reason the test is a ratio.
        new CoastlineService(geometryFactory).AddArcs(
            [CreatePolygonWithHole(geometryFactory, 10, 45, 90, 0.2)],
            arcGraph);

        arcGraph.Arcs.Should().ContainSingle();
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldThrowArgumentNullException_WhenArcGraphIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);

        Action action = () => new CoastlineService(geometryFactory).AddArcs([], null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldThrowArgumentNullException_WhenPolygonsAreNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);

        Action action = () => new CoastlineService(geometryFactory)
            .AddArcs(null!, new ArcGraph(coordinateSequenceFactory));

        action.Should().Throw<ArgumentNullException>();
    }

    private static GeometryFactory CreateGeometryFactory(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Matches the fixed model Program.cs installs, rather than letting the fixture
        // invent a scale that would quantise the coordinates used here.
        return new GeometryFactory(new PrecisionModel(10000.0), 0, coordinateSequenceFactory);
    }

    private static Polygon CreatePolygonWithHole(
        GeometryFactory geometryFactory,
        double holeX,
        double holeY,
        double holeWidth,
        double holeHeight)
    {
        return geometryFactory.CreatePolygon(
            CreateRing(geometryFactory, 0, 0, 100, 100),
            [CreateRing(geometryFactory, holeX, holeY, holeWidth, holeHeight)]);
    }

    private static LinearRing CreateRing(
        GeometryFactory geometryFactory,
        double x,
        double y,
        double width,
        double height)
    {
        return geometryFactory.CreateLinearRing(
            [
                new Coordinate(x, y),
                new Coordinate(x + width, y),
                new Coordinate(x + width, y + height),
                new Coordinate(x, y + height),
                new Coordinate(x, y),
            ]);
    }

    private static Polygon CreateSquare(GeometryFactory geometryFactory, double x, double y, double size)
    {
        return geometryFactory.CreatePolygon(CreateRing(geometryFactory, x, y, size, size));
    }
}
