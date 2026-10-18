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
[TestOf(typeof(CorrectionService))]
public class CorrectionServiceCollapseTests
{
    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldCutOutTheRepeat_WhenOneArcRepeatsAnother(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (20, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(2, 0.05), (12, 0.05)]);

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        arcGraph.Arcs.Should().ContainSingle("the shorter copy carries nothing the longer one does not");
        arcGraph.Arcs[0].GetFullCoordinates().Should().HaveCount(3);
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldLeaveOneCopy_WhenTheSameBorderIsDrawnFourTimes(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Four byte-identical copies of one path, which is what copy and paste in the
        // drawing leaves behind.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        for (int i = 0; i < 4; ++i)
        {
            AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (20, 0)]);
        }

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        arcGraph.Arcs.Should().ContainSingle();
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldKeepTheStretchBeyondTheRepeat_WhenOnlyPartOfAnArcRepeats(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The real case: one arc redraws part of another and then carries on somewhere of
        // its own. Only the part that repeats may go.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (100, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 0.05), (20, 0.05), (20, 40)]);

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        arcGraph.Arcs.Should().HaveCount(2);
        Arc remainder = arcGraph.Arcs.Single(arc => Array.Exists(arc.GetFullCoordinates(), c => c.Y >= 40.0));
        remainder.StartNode.Coordinate.X.Should().BeApproximately(20.0, 1E-9);
        remainder.StartNode.Coordinate.Y.Should().BeApproximately(0.05, 1E-9);
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldLeaveTheEndForWeldingToResolve_WhenTheRepeatCarriedAJunction(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The reason the repeat is cut rather than deleted: another border ended on it, and
        // that border has to end on the surviving copy instead. Without it the two faces
        // either side of the stem would fuse, which is the defect being repaired.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (100, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 0.05), (50, 0.05)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 20), (10, 0.1)]);

        CorrectionService service = CreateService(coordinateSequenceFactory);
        service.Collapse(arcGraph, 0.25);
        service.Weld(arcGraph, 1.0, 1.0);

        arcGraph.Arcs.Should().HaveCount(3, "the long arc is split where the stem now meets it");
        Arc stem = arcGraph.Arcs.Single(arc => Array.Exists(arc.GetFullCoordinates(), c => c.Y >= 20.0));
        Node junction = stem.EndNode;
        arcGraph.Arcs.Count(arc => (arc.StartNode == junction) || (arc.EndNode == junction))
            .Should()
            .Be(3, "both halves of the split arc and the stem meet there");
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldLeaveBothAlone_WhenTheyOnlyRunTogetherBriefly(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // Two borders leaving the same junction keep company for a vertex or two. That is
        // not a repeat, and the minimum run length is what tells the two apart.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (100, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0.1), (1, 0.1), (10, 20)]);

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        arcGraph.Arcs.Should().HaveCount(2);
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldLeaveBothAlone_WhenTheyAreFurtherApartThanTheTolerance(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (100, 0)]);
        AddArc(arcGraph, coordinateSequenceFactory, [(10, 0.3), (50, 0.3)]);

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        arcGraph.Arcs.Should().HaveCount(2);
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldCutOutTheNeedle_WhenAnArcRunsOutAndBackToTheSamePoint(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The arc leaves (10, 0), runs out along a straight line, and comes back to exactly
        // (10, 0) before carrying on. That stretch has drawn a line and not a boundary: nothing
        // can be on one side of it, because both of its sides are the same side. Left in, 5a
        // nodes the arc against its own other leg and the border comes out in pieces that no
        // traversal can divide anything with, which is how Kalimantan and Daha came out as one
        // face. The vertices are spaced differently on the way back, as they are in the source
        // map, so this is not caught by comparing one point with the one before it.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            [(0, 0), (10, 0), (11, 0), (12, 0), (13, 0), (11.5, 0), (10, 0), (10, 10)]);

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.GetFullCoordinates()
            .Select(coordinate => (coordinate.X, coordinate.Y))
            .Should()
            .Equal((0.0, 0.0), (10.0, 0.0), (10.0, 10.0));
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldKeepTheLoop_WhenAnArcComesBackToAPointAroundSomething(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The other side of that rule. An arc that returns to a point it has already visited and
        // encloses something on the way has drawn a loop, which has an inside and an outside and
        // is boundary like any other. Only a loop enclosing nothing goes.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            [(0, 0), (10, 0), (12, 2), (14, 0), (12, -2), (10, 0), (10, 10)]);

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.GetFullCoordinates().Should().HaveCount(7);
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldCutOutTheNeedle_WhenTheArcReversesAndComesBackBetweenTwoVertices(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // A cusp left by a badly drawn bezier. The arc runs out along the x axis, turns through
        // 180 degrees at (10, 0), and comes back to (3, 0) - which lies on the leg it went out on
        // but is not one of that leg's vertices. No coordinate repeats, so looking for a point
        // visited twice finds nothing, and the three bridges 5h could not account for were made
        // of exactly this.
        //
        // What the run draws is the segment from (0, 0) to (3, 0), walked forwards, backwards and
        // forwards again, so that is what it is replaced by.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(
            arcGraph,
            coordinateSequenceFactory,
            [(0, 10), (0, 0), (4, 0), (8, 0), (10, 0), (6, 0), (3, 0), (3, -5)]);

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.GetFullCoordinates()
            .Select(coordinate => (coordinate.X, coordinate.Y))
            .Should()
            .Equal((0.0, 10.0), (0.0, 0.0), (3.0, 0.0), (3.0, -5.0));
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldKeepTheCorner_WhenTheArcTurnsSharplyWithoutRunningBackOnItself(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The limit of that rule, and the reason it asks for one line rather than a sharp angle.
        // 169 vertices on the sample map turn by more than 150 degrees and only 14 of them are
        // cusps; the other 155 are corners like this one, where the arc comes back at 174 degrees
        // but along its own route. A corner is drawn boundary and stays.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (0, 1)]);

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.GetFullCoordinates().Should().HaveCount(3);
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldKeepTheSliver_WhenAReversalsTwoLegsAreNotOnOneLine(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The guard the rule rests on. Half a degree off straight counts as a reversal by the
        // angle, which is all the walk out from the cusp uses, but the far end of this run stands
        // 10 units off the line joining the two ends. Collapsing it would move the boundary, so
        // the deviation is measured against that line and this is left as it is.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (0, 0.08)]);

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.GetFullCoordinates().Should().HaveCount(3);
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldDropTheRepeatedVertex_WhenAPointIsDrawnTwiceOver(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The degenerate end of the same rule: a loop of no length at all, which is one vertex
        // written twice where the drawing stopped and started again. 16 of these on the sample
        // map against 9 needles with real length.
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        AddArc(arcGraph, coordinateSequenceFactory, [(0, 0), (10, 0), (10, 0), (10, 10)]);

        CreateService(coordinateSequenceFactory).Collapse(arcGraph, 0.25);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.GetFullCoordinates().Should().HaveCount(3);
    }

    [AutoCustomizedData]
    [Test]
    public void Collapse_ShouldThrowArgumentNullException_WhenArcGraphIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => CreateService(coordinateSequenceFactory).Collapse(null!, 0.25);

        action.Should().Throw<ArgumentNullException>();
    }

    private static void AddArc(
        ArcGraph arcGraph,
        CoordinateSequenceFactory coordinateSequenceFactory,
        (double X, double Y)[] points)
    {
        arcGraph.AddArc(
            ArcKind.Border,
            coordinateSequenceFactory.Create(points.Select(point => new Coordinate(point.X, point.Y)).ToArray()));
    }

    private static CorrectionService CreateService(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new CorrectionService(new GeometryFactory(new PrecisionModel(10000.0), 0, coordinateSequenceFactory));
    }
}
