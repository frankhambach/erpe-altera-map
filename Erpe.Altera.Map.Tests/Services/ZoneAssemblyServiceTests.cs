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
[TestOf(typeof(ZoneAssemblyService))]
public class ZoneAssemblyServiceTests
{
    // A map of roughly the source's proportions, so that a unit of it is a few kilometres.
    private static readonly Envelope SourceEnvelope = new Envelope(0.0, 6000.0, 0.0, 3700.0);

    [AutoCustomizedData]
    [Test]
    public void Assemble_ShouldGiveEachCountryTheSeaOnItsOwnSide_WhenABorderCutsAnIslandInTwo(
        CoordinateSequenceFactory coordinateSequenceFactory)
    {
        // An island 100 units by 40, about 650 km by 260, cut in two down its middle. West's zone
        // is the sea west of the median line that leaves the island where the border does, East's
        // the sea east of it, neither covers the land, and the ocean a couple of thousand
        // kilometres off is nobody's.
        GeometryFactory geometryFactory = new GeometryFactory();
        WinkelTripelProjectionService projectionService = new WinkelTripelProjectionService(geometryFactory);
        ArcGraph arcGraph = new ArcGraph(coordinateSequenceFactory);
        Country west = new Country("West", CountryType.UnitaryState);
        Country east = new Country("East", CountryType.UnitaryState);
        Node top = arcGraph.AddNode(new Coordinate(3000.0, 1830.0));
        Node bottom = arcGraph.AddNode(new Coordinate(3000.0, 1870.0));

        // Walked as given, the western coast has the land on its right and the eastern on its left,
        // y running down the page: so the land face walks the one reversed and the other not.
        Shore[] shores =
        [
            CreateCoast(projectionService, arcGraph, west, top, bottom, [(2950.0, 1830.0), (2950.0, 1870.0)], true),
            CreateCoast(projectionService, arcGraph, east, top, bottom, [(3050.0, 1830.0), (3050.0, 1870.0)], false),
        ];
        EconomicZoneService economicZoneService = new EconomicZoneService(projectionService);
        TopologyService topologyService = new TopologyService(geometryFactory);
        ZoneAssemblyService service = new ZoneAssemblyService(
            new RimService(geometryFactory, projectionService),
            topologyService,
            new AssemblyService(geometryFactory, topologyService));

        IReadOnlyList<Country> zones = service.Assemble(
            economicZoneService.AddArcs(
                arcGraph,
                shores,
                economicZoneService.GetBoundaries(shores, SourceEnvelope),
                SourceEnvelope),
            arcGraph,
            shores,
            SourceEnvelope);

        zones.Select(zone => zone.Id).Should().BeEquivalentTo("West", "East");
        GetOwner(zones, geometryFactory, 2900.0, 1850.0).Should().Be("West");
        GetOwner(zones, geometryFactory, 3100.0, 1850.0).Should().Be("East");
        GetOwner(zones, geometryFactory, 2975.0, 1850.0).Should().BeNull("the island is land and in no zone");
        GetOwner(zones, geometryFactory, 3025.0, 1850.0).Should().BeNull("the island is land and in no zone");
        GetOwner(zones, geometryFactory, 3000.0, 1500.0).Should().BeNull("that far out is the high seas");
    }

    [Test]
    public void Assemble_ShouldThrowArgumentNullException_WhenTheZonesAreNull()
    {
        GeometryFactory geometryFactory = new GeometryFactory();
        TopologyService topologyService = new TopologyService(geometryFactory);
        ZoneAssemblyService service = new ZoneAssemblyService(
            new RimService(geometryFactory, new WinkelTripelProjectionService(geometryFactory)),
            topologyService,
            new AssemblyService(geometryFactory, topologyService));

        Action action = () => service.Assemble(null!, new ArcGraph(geometryFactory.CoordinateSequenceFactory), [], SourceEnvelope);

        action.Should().Throw<ArgumentNullException>();
    }

    // A coast arc between two nodes, drawn in the source map's coordinates for the land graph and
    // carried in longitude and latitude for the zones, as ConvertCommand hands it over.
    private static Shore CreateCoast(
        WinkelTripelProjectionService projectionService,
        ArcGraph arcGraph,
        Country owner,
        Node start,
        Node end,
        (double X, double Y)[] interior,
        bool isLandReversed)
    {
        Arc arc = arcGraph.AddArc(
            ArcKind.Coast,
            start,
            end,
            arcGraph.CoordinateSequenceFactory.Create([.. interior.Select(point => new Coordinate(point.X, point.Y))]));

        return new Shore(
            owner,
            arc,
            [
                .. arc.GetFullCoordinates()
                    .Select(coordinate => projectionService.Unproject(projectionService.Rescale(coordinate, SourceEnvelope))),
            ],
            0.0,
            isLandReversed);
    }

    private static string? GetOwner(IEnumerable<Country> zones, GeometryFactory geometryFactory, double x, double y)
    {
        Point point = geometryFactory.CreatePoint(new Coordinate(x, y));

        return zones.FirstOrDefault(zone => zone.Shapes.Any(shape => shape.Polygon.Contains(point)))?.Id;
    }
}
