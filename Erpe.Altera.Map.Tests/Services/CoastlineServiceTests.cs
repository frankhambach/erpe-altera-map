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
    public void AddArcs_ShouldDiscardHole_WhenABorderRunsThroughItsWholeLength(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // 20 long and half a unit wide comes to 0.24 on the area to length ratio, above the
        // 0.15 that tells a gap from a lake, so the ratio alone keeps this as water. A border
        // running its whole length says otherwise: that is the gap between two shapes that
        // should have met along it. Padun and Kunmin shared a face for exactly this reason.
        arcGraph.AddArc(
            ArcKind.Border,
            coordinateSequenceFactory.Create([new Coordinate(5.0, 45.25), new Coordinate(35.0, 45.25)]));

        new CoastlineService(geometryFactory).AddArcs(
            [CreatePolygonWithHole(geometryFactory, 10.0, 45.0, 20.0, 0.5)],
            arcGraph);

        arcGraph.Arcs.Should().NotContain(arc => arc.Kind == ArcKind.Lake);
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldKeepHole_WhenABorderOnlyReachesIntoIt(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // The same hole, with half a unit of border reaching into it rather than twenty running
        // along it. Borders are drawn over water all the time, which 2c-bis trims back, and
        // that must not be read as the hole being land.
        arcGraph.AddArc(
            ArcKind.Border,
            coordinateSequenceFactory.Create([new Coordinate(5.0, 45.25), new Coordinate(10.5, 45.25)]));

        new CoastlineService(geometryFactory).AddArcs(
            [CreatePolygonWithHole(geometryFactory, 10.0, 45.0, 20.0, 0.5)],
            arcGraph);

        arcGraph.Arcs.Should().Contain(arc => arc.Kind == ArcKind.Lake);
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldFillSlit_WhenAGapBetweenTwoShapesReachesTheSea(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // Two shapes that should have met along x = 40 leave a wedge between them 0.03 wide
        // and 20 deep. Because the wedge opens onto the sea the union keeps it as a slit in
        // the shell, where the gap test only ever sees a hole, and the ground beside it is
        // left enclosed by nothing.
        double area = new CoastlineService(geometryFactory).AddArcs(
            [CreateNotchedSquare(geometryFactory, 40.0, 0.03, 20.0)],
            arcGraph);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.GetFullCoordinates().Should().NotContain(coordinate => (coordinate.Y > 0.0) && (coordinate.Y < 100.0));

        // The ground the slit used to take out of the land counts towards what the faces
        // have to come to, since the coast no longer goes round it.
        area.Should().BeApproximately(10000.0, 0.01);
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldKeepInlet_WhenTheRunEnclosesEnoughWater(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // 0.45 wide and 1.2 deep comes to 0.19 on the area to length ratio, where the gaps
        // between shapes reach 0.054 at the widest. That is a narrow inlet of sea, and the
        // coast has to keep going round it.
        new CoastlineService(geometryFactory).AddArcs(
            [CreateNotchedSquare(geometryFactory, 40.0, 0.45, 1.2)],
            arcGraph);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.GetFullCoordinates().Should().Contain(coordinate => (coordinate.Y > 1.0) && (coordinate.Y < 99.0));
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldKeepSpit_WhenTheRunEnclosesLand(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // The same shape the other way up: a spit 0.03 wide reaching 20 units out to sea.
        // It doubles back on itself exactly as a slit does and is every bit as thin, but it
        // encloses land, and however thin it is it belongs to the country.
        new CoastlineService(geometryFactory).AddArcs(
            [CreateNotchedSquare(geometryFactory, 40.0, 0.03, -20.0)],
            arcGraph);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.GetFullCoordinates().Should().Contain(coordinate => coordinate.Y < 0.0);
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

    private static Polygon CreateNotchedSquare(
        GeometryFactory geometryFactory,
        double notchX,
        double notchWidth,
        double notchDepth)
    {
        return geometryFactory.CreatePolygon(
            geometryFactory.CreateLinearRing(
                [
                    new Coordinate(0.0, 0.0),
                    new Coordinate(notchX, 0.0),
                    new Coordinate(notchX, notchDepth),
                    new Coordinate(notchX + notchWidth, notchDepth),
                    new Coordinate(notchX + notchWidth, 0.0),
                    new Coordinate(100.0, 0.0),
                    new Coordinate(100.0, 100.0),
                    new Coordinate(0.0, 100.0),
                    new Coordinate(0.0, 0.0),
                ]));
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
