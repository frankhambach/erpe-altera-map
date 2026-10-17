// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Services;

using System;

using Svg;

public static class SvgElementExtensions
{
    // The elements this pipeline reads linework from, less the ones the author has hidden.
    //
    // All four kinds carry linework the approximation service can turn into coordinates, and
    // nothing else in the document does: a group, a label or a clip path has no line of its
    // own. `SvgPolyline` needs no case of its own, since it derives from `SvgPolygon`.
    //
    // A hidden element is one the author has said is not part of the map, and reading it
    // anyway makes work that has to be undone further down: the two hidden paths in
    // Border_Tracers are one line drawn twice, and 1b was spending its duplicate rule
    // collapsing the second onto the first to arrive back where the drawing already was.
    //
    // One rule in one place because the document is read in three: the tracer layers in
    // Phase 1, and the rim and the country shapes in `ConvertCommand`. The three had the same
    // type test written out separately and only the first of them asked about `display`.
    //
    // Known limitation: this asks the element and not its ancestors, so linework inside a
    // hidden *group* is still read. That is how Illustrator hides a whole layer, so it is
    // worth having, but nothing on this map is hidden that way - both hidden elements here
    // are paths - and guessing at the shape of a case no export has yet shown would be
    // inventing the fix rather than measuring it.
    public static bool IsDrawnLinework(this SvgElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        return element is SvgPath or SvgLine or SvgRectangle or SvgPolygon
            && (!element.TryGetAttribute("display", out string? display) || (display != "none"));
    }
}
