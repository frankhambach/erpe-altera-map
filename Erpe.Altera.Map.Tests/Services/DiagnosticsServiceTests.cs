// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Tests.Services;

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;

using Erpe.Altera.Map.Models;
using Erpe.Altera.Map.Services;
using Erpe.Testing;

using FluentAssertions;

using NetTopologySuite.Geometries;

using NUnit.Framework;

[TestFixture]
[TestOf(typeof(DiagnosticsService))]
public class DiagnosticsServiceTests
{
    [AutoCustomizedData]
    [Test]
    public async Task WriteArcsAsync_ShouldWriteEveryArcWithItsIdentityAndKind(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        arcGraph.AddArc(ArcKind.Coast, coordinateSequenceFactory.Create(CreateRing(0, 0, 60)));
        arcGraph.AddArc(ArcKind.Lake, coordinateSequenceFactory.Create(CreateRing(10, 10, 20)));
        FileInfo file = CreateFile();

        try
        {
            await new DiagnosticsService().WriteArcsAsync(arcGraph.Arcs, [], null, file)
                .ConfigureAwait(false);

            XElement[] polylines = GetElements(file, "polyline");
            polylines.Should().HaveCount(2);
            polylines.Select(polyline => polyline.Attribute("data-arc-id")?.Value)
                .Should()
                .BeEquivalentTo("0", "1");
            polylines.Select(polyline => polyline.Attribute("data-kind")?.Value)
                .Should()
                .BeEquivalentTo("Coast", "Lake");
            GetElements(file, "circle").Should().HaveCount(2, "each ring closes on a node of its own");
        }
        finally
        {
            file.Delete();
        }
    }

    [AutoCustomizedData]
    [Test]
    public async Task WriteFacesAsync_ShouldWriteEveryFaceWithItsIdentityAndOwner(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        arcGraph.AddArc(ArcKind.Coast, coordinateSequenceFactory.Create(CreateRing(0, 0, 60)));
        arcGraph.AddArc(ArcKind.Lake, coordinateSequenceFactory.Create(CreateRing(10, 10, 20)));
        new TopologyService(geometryFactory).BuildFaces(arcGraph, 0.0);
        arcGraph.Faces[0].Owner = new Country("Nowhere", CountryType.UnitaryState, []);
        FileInfo file = CreateFile();

        try
        {
            await new DiagnosticsService().WriteFacesAsync(arcGraph.Faces, file).ConfigureAwait(false);

            XElement[] paths = GetElements(file, "path");
            paths.Should().HaveCount(arcGraph.Faces.Count);
            paths.Select(path => path.Attribute("data-face-id")?.Value)
                .Should()
                .BeEquivalentTo(arcGraph.Faces.Select(face => face.Id.ToString(CultureInfo.InvariantCulture)));
            paths.Count(path => path.Attribute("data-owner")?.Value == "Nowhere")
                .Should()
                .Be(1, "the one face that was given an owner says so and the rest stay anonymous");
        }
        finally
        {
            file.Delete();
        }
    }

    [AutoCustomizedData]
    [Test]
    public async Task WriteFacesAsync_ShouldWriteTheHolesAsFurtherSubpaths_WhenAFaceHasThem(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // The hole has to come through as part of its own face, because a face written
        // without it would cover the lake it is meant to leave out.
        GeometryFactory geometryFactory = CreateGeometryFactory(coordinateSequenceFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        arcGraph.AddArc(ArcKind.Coast, coordinateSequenceFactory.Create(CreateRing(0, 0, 60)));
        arcGraph.AddArc(ArcKind.Lake, coordinateSequenceFactory.Create(CreateRing(10, 10, 20)));
        new TopologyService(geometryFactory).BuildFaces(arcGraph, 0.0);
        FileInfo file = CreateFile();

        try
        {
            await new DiagnosticsService().WriteFacesAsync(arcGraph.Faces, file).ConfigureAwait(false);

            GetElements(file, "path")
                .Select(path => path.Attribute("d")?.Value.Count(character => character == 'Z'))
                .Should()
                .BeEquivalentTo([2, 1], "the outer face carries the lake as a hole and the lake carries none");
        }
        finally
        {
            file.Delete();
        }
    }

    [Test]
    public async Task WriteArcsAsync_ShouldThrowArgumentNullException_WhenTheArcsAreNull()
    {
        Func<Task> action = () => new DiagnosticsService().WriteArcsAsync(null!, [], null, CreateFile());

        await action.Should().ThrowAsync<ArgumentNullException>().ConfigureAwait(false);
    }

    private static FileInfo CreateFile()
    {
        return new FileInfo(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".svg"));
    }

    private static Coordinate[] CreateRing(double x, double y, double size)
    {
        return
        [
            new Coordinate(x, y),
            new Coordinate(x + size, y),
            new Coordinate(x + size, y + size),
            new Coordinate(x, y + size),
            new Coordinate(x, y),
        ];
    }

    private static GeometryFactory CreateGeometryFactory(CoordinateSequenceFactory coordinateSequenceFactory)
    {
        return new GeometryFactory(new PrecisionModel(10000.0), 0, coordinateSequenceFactory);
    }

    // Loaded as XML rather than searched as text, which is also the check that what was
    // written is well formed.
    private static XElement[] GetElements(FileInfo file, string name)
    {
        return
        [
            .. XDocument.Load(file.FullName)
                .Descendants()
                .Where(element => string.Equals(element.Name.LocalName, name, StringComparison.Ordinal)),
        ];
    }
}
