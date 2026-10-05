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
        service.Weld(arcGraph, 1.0);

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
