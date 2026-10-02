// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

using Erpe.Altera.Map.Contracts;
using Erpe.Altera.Map.Models;

using NetTopologySuite.Geometries;

using Svg;

public sealed class TracerService : ITracerService
{
    private static readonly ImmutableDictionary<string, ArcKind> TracerLayers =
        ImmutableDictionary<string, ArcKind>.Empty.Add("Border_Tracers", ArcKind.Border)
            .Add("Substate_Tracers", ArcKind.Substate);

    private readonly ISvgApproximationService svgApproximationService;

    public TracerService(ISvgApproximationService svgApproximationService)
    {
        this.svgApproximationService = svgApproximationService;
    }

    public void AddArcs(SvgDocument document, ArcGraph arcGraph)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(arcGraph);

        // Both layers go into the same graph, so that a substate border coinciding with a
        // national border collapses onto it when the graph is noded rather than being
        // carried as a second, slightly different line.
        foreach (SvgElement layerElement in document.Descendants()
                     .Where(element => (element.ID != null) && TracerLayers.ContainsKey(element.ID)))
        {
            ArcKind kind = TracerLayers[layerElement.ID];
            foreach (CoordinateSequence coordinateSequence in layerElement.Descendants()
                         .Where(element => element is SvgPath or SvgLine or SvgRectangle or SvgPolygon)
                         .SelectMany(element => this.svgApproximationService.Approximate(element))
                         .Where(coordinateSequence => coordinateSequence.Count >= 2))
            {
                arcGraph.AddArc(kind, coordinateSequence);
            }
        }
    }
}
