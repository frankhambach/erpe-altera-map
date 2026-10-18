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
        // along it. Borders are drawn over water all the time, which 3f trims back, and
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

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldCutTheNeedle_WhenALakeShoreDrawsOneLineTwice(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // Two shapes disagreeing about the water's edge by 0.0002 units leave the lake shore
        // walking half a unit out and back, which encloses 5e-05 over a run of 1. Whatever is
        // assembled from the ring then walks that half unit twice, once in each direction,
        // which is Dematia's segment used twice by one country.
        new CoastlineService(geometryFactory).AddArcs(
            [CreateLakeWithANeedle(geometryFactory, 0.0002)],
            arcGraph);

        arcGraph.Arcs.Should()
            .ContainSingle(arc => arc.Kind == ArcKind.Lake)
            .Which.GetFullCoordinates()
            .Should()
            .NotContain(
                coordinate => coordinate.Equals2D(new Coordinate(44.5, 50.0)),
                "the tip of the needle is the only vertex that was out there");
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldKeepTheRun_WhenALakeShoreComesBackRoundSomething(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // The same run 0.02 units wide rather than 0.0002, which encloses 0.005 over the same
        // run of 1 and is a shape rather than a line drawn twice. A ring comes back on itself
        // all over this map and almost always has something inside it when it does.
        new CoastlineService(geometryFactory).AddArcs([CreateLakeWithANeedle(geometryFactory, 0.02)], arcGraph);

        arcGraph.Arcs.Should()
            .ContainSingle(arc => arc.Kind == ArcKind.Lake)
            .Which.GetFullCoordinates()
            .Should()
            .Contain(
                coordinate => coordinate.Equals2D(new Coordinate(44.5, 50.0)),
                "what the run encloses is what says whether it is a needle");
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldCutTheTracerBack_WhenItIsDrawnAlongASlitInTheShore(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // The border between the two shapes, drawn down the left wall of the slit they leave
        // where they reach the sea. The slit comes back after 0.81 units, under the 1 unit the
        // fill asks for, so it stays in the shell - and the border and the shore then carry the
        // same 0.4 units twice over, which is a segment three rings use at once.
        arcGraph.AddArc(
            ArcKind.Border,
            coordinateSequenceFactory.Create([new Coordinate(50.0, 0.0), new Coordinate(50.0, 20.0)]));

        new CoastlineService(geometryFactory).AddArcs(CreateShapesWithASlit(geometryFactory), arcGraph);

        Arc border = arcGraph.Arcs.Should().ContainSingle(arc => arc.Kind == ArcKind.Border).Subject;
        border.GetFullCoordinates()[0].Y.Should().BeApproximately(0.4, 0.01, "the stretch in the water is gone");
        border.GetFullCoordinates()[^1].Y.Should().Be(20.0, "the stretch inland is not the cut's to take");
        arcGraph.Arcs.Should()
            .ContainSingle(arc => arc.Kind == ArcKind.Coast)
            .Which.GetFullCoordinates()
            .Should()
            .Contain(
                coordinate => coordinate.Equals2D(new Coordinate(50.0, 0.4)),
                "the shore keeps the slit; it is the border that was drawn in the water");
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldLeaveTheTracerWhole_WhenItOnlyEndsAtTheShore(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // A border reaching the coast crosses the margin the cut is made against and loses a
        // sliver of its last segment to it. That is every border that reaches the sea, and
        // pulling them all back by a hair was measured to cost two countries their geometry.
        arcGraph.AddArc(
            ArcKind.Border,
            coordinateSequenceFactory.Create([new Coordinate(25.0, 0.0), new Coordinate(25.0, 20.0)]));

        new CoastlineService(geometryFactory).AddArcs(CreateShapesWithASlit(geometryFactory), arcGraph);

        arcGraph.Arcs.Should()
            .ContainSingle(arc => arc.Kind == ArcKind.Border)
            .Which.GetFullCoordinates()[0]
            .Equals2D(new Coordinate(25.0, 0.0))
            .Should()
            .BeTrue("the end is on the shore, which is where a border should end");
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldDropThePiece_WhenTheCutLeavesItSeawardOfTheCoast(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);

        // The same border, drawn a unit out to sea before it comes ashore and runs up the slit.
        // Cutting the run out leaves that unit with the water on both sides of it, bounding
        // nothing, so it goes with the run rather than being left to dangle.
        arcGraph.AddArc(
            ArcKind.Border,
            coordinateSequenceFactory.Create(
                [new Coordinate(50.0, -1.0), new Coordinate(50.0, 0.0), new Coordinate(50.0, 20.0)]));

        new CoastlineService(geometryFactory).AddArcs(CreateShapesWithASlit(geometryFactory), arcGraph);

        arcGraph.Arcs.Should().ContainSingle(arc => arc.Kind == ArcKind.Border);
        arcGraph.Arcs.Where(arc => arc.Kind == ArcKind.Border)
            .SelectMany(arc => arc.GetFullCoordinates())
            .Should()
            .OnlyContain(coordinate => coordinate.Y > 0.0);
    }

    // A 10 by 10 lake, fat enough for 3d to keep as water, whose shore runs half a unit west
    // from 45, 50 and comes back `width` units away from where it set out.
    private static Polygon CreateLakeWithANeedle(GeometryFactory geometryFactory, double width)
    {
        return geometryFactory.CreatePolygon(
            CreateRing(geometryFactory, 0, 0, 100, 100),
            [
                geometryFactory.CreateLinearRing(
                    [
                        new Coordinate(40.0, 40.0),
                        new Coordinate(50.0, 40.0),
                        new Coordinate(50.0, 50.0),
                        new Coordinate(45.0, 50.0),
                        new Coordinate(44.5, 50.0),
                        new Coordinate(45.0, 50.0 + width),
                        new Coordinate(40.0, 50.0),
                        new Coordinate(40.0, 40.0),
                    ]),
            ]);
    }

    // Two shapes that meet along x = 50 and part company where they reach the sea at y = 0,
    // leaving a wedge 0.1 units wide at the mouth and 0.4 deep. The union walks up the left
    // wall and back down the right, which is a slit in the shell 0.81 units long.
    private static Polygon[] CreateShapesWithASlit(GeometryFactory geometryFactory)
    {
        return
        [
            geometryFactory.CreatePolygon(
                geometryFactory.CreateLinearRing(
                    [
                        new Coordinate(0.0, 0.0),
                        new Coordinate(50.0, 0.0),
                        new Coordinate(50.0, 100.0),
                        new Coordinate(0.0, 100.0),
                        new Coordinate(0.0, 0.0),
                    ])),
            geometryFactory.CreatePolygon(
                geometryFactory.CreateLinearRing(
                    [
                        new Coordinate(50.1, 0.0),
                        new Coordinate(100.0, 0.0),
                        new Coordinate(100.0, 100.0),
                        new Coordinate(50.0, 100.0),
                        new Coordinate(50.0, 0.4),
                        new Coordinate(50.1, 0.0),
                    ])),
        ];
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
