// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System;
using System.Drawing;
using System.Linq;

using Erpe.Altera.Map.Models;
using Erpe.Altera.Map.Services;
using Erpe.Testing;

using FluentAssertions;

using NetTopologySuite.Geometries;

using NUnit.Framework;

using Svg;
using Svg.Pathing;

[TestFixture]
[TestOf(typeof(TracerService))]
public class TracerServiceTests
{
    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldAddBorderAndSubstateArcs_WhenBothTracerLayersArePresent(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgDocument document = CreateDocument(
            ("Border_Tracers", CreateLine(0, 0, 10, 0)),
            ("Substate_Tracers", CreateLine(0, 5, 10, 5)));
        ArcGraph arcGraph = new ArcGraph();

        CreateService(coordinateSequenceFactory).AddArcs(document, arcGraph);

        arcGraph.Arcs.Select(arc => arc.Kind)
            .Should()
            .BeEquivalentTo([ArcKind.Border, ArcKind.Substate]);
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldHoldOnlyInteriorCoordinates_WhenArcIsOpen(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgDocument document = CreateDocument(
            ("Border_Tracers",
                CreatePath(
                    new SvgMoveToSegment(false, new PointF(0, 0)),
                    new SvgLineSegment(false, new PointF(10, 0)),
                    new SvgLineSegment(false, new PointF(10, 10)))));
        ArcGraph arcGraph = new ArcGraph();

        CreateService(coordinateSequenceFactory).AddArcs(document, arcGraph);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.StartNode.Coordinate.Should().Be(new Coordinate(0, 0));
        arc.EndNode.Coordinate.Should().Be(new Coordinate(10, 10));
        arc.Coordinates.ToCoordinateArray().Should().BeEquivalentTo([new Coordinate(10, 0)]);
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldHoldNoCoordinates_WhenArcHasNoInteriorVertices(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgDocument document = CreateDocument(("Border_Tracers", CreateLine(0, 0, 10, 0)));
        ArcGraph arcGraph = new ArcGraph();

        CreateService(coordinateSequenceFactory).AddArcs(document, arcGraph);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.Coordinates.Count.Should().Be(0);
        arc.StartNode.Coordinate.Should().Be(new Coordinate(0, 0));
        arc.EndNode.Coordinate.Should().Be(new Coordinate(10, 0));
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldShareOneNode_WhenArcIsClosed(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgDocument document = CreateDocument(
            ("Border_Tracers",
                CreatePath(
                    new SvgMoveToSegment(false, new PointF(0, 0)),
                    new SvgLineSegment(false, new PointF(10, 0)),
                    new SvgLineSegment(false, new PointF(10, 10)),
                    new SvgClosePathSegment(false))));
        ArcGraph arcGraph = new ArcGraph();

        CreateService(coordinateSequenceFactory).AddArcs(document, arcGraph);

        Arc arc = arcGraph.Arcs.Should().ContainSingle().Subject;
        arc.IsClosed.Should().BeTrue();
        arc.StartNode.Should().BeSameAs(arc.EndNode);
        arcGraph.Nodes.Should().ContainSingle();
        arc.Coordinates.ToCoordinateArray()
            .Should()
            .BeEquivalentTo([new Coordinate(10, 0), new Coordinate(10, 10)], options => options.WithStrictOrdering());
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldIgnoreElements_WhenTheyAreOutsideATracerLayer(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgDocument document = CreateDocument(
            ("Country_Shapes", CreateLine(0, 0, 10, 0)),
            ("Rim", CreateLine(0, 5, 10, 5)));
        ArcGraph arcGraph = new ArcGraph();

        CreateService(coordinateSequenceFactory).AddArcs(document, arcGraph);

        arcGraph.Arcs.Should().BeEmpty();
        arcGraph.Nodes.Should().BeEmpty();
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldAssignDistinctIdentifiers_WhenSeveralArcsAreAdded(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        SvgDocument document = CreateLayer("Border_Tracers", CreateLine(0, 0, 10, 0), CreateLine(0, 5, 10, 5));
        ArcGraph arcGraph = new ArcGraph();

        CreateService(coordinateSequenceFactory).AddArcs(document, arcGraph);

        arcGraph.Arcs.Select(arc => arc.Id).Should().OnlyHaveUniqueItems().And.HaveCount(2);
        arcGraph.Nodes.Select(node => node.Id).Should().OnlyHaveUniqueItems().And.HaveCount(4);
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldThrowArgumentNullException_WhenArcGraphIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => CreateService(coordinateSequenceFactory).AddArcs(new SvgDocument(), null!);

        action.Should().Throw<ArgumentNullException>();
    }

    [AutoCustomizedData]
    [Test]
    public void AddArcs_ShouldThrowArgumentNullException_WhenDocumentIsNull(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        Action action = () => CreateService(coordinateSequenceFactory).AddArcs(null!, new ArcGraph());

        action.Should().Throw<ArgumentNullException>();
    }

    private static SvgDocument CreateDocument(params (string LayerId, SvgElement Element)[] layers)
    {
        SvgDocument document = new SvgDocument();
        foreach ((string layerId, SvgElement element) in layers)
        {
            SvgGroup group = new SvgGroup
                {
                    ID = layerId,
                };
            group.Children.Add(element);
            document.Children.Add(group);
        }

        return document;
    }

    private static SvgDocument CreateLayer(string layerId, params SvgElement[] elements)
    {
        SvgDocument document = new SvgDocument();
        SvgGroup group = new SvgGroup
            {
                ID = layerId,
            };
        foreach (SvgElement element in elements)
        {
            group.Children.Add(element);
        }

        document.Children.Add(group);

        return document;
    }

    private static SvgLine CreateLine(float startX, float startY, float endX, float endY)
    {
        return new SvgLine
            {
                StartX = new SvgUnit(startX),
                StartY = new SvgUnit(startY),
                EndX = new SvgUnit(endX),
                EndY = new SvgUnit(endY),
            };
    }

    private static SvgPath CreatePath(params SvgPathSegment[] segments)
    {
        SvgPath path = new SvgPath
            {
                PathData = [],
            };
        foreach (SvgPathSegment segment in segments)
        {
            path.PathData.Add(segment);
        }

        return path;
    }

    private static TracerService CreateService(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new TracerService(
            new SvgApproximationService(coordinateSequenceFactory),
            coordinateSequenceFactory);
    }
}
