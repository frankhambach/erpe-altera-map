// Copyright (c) Frank Hambach. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Erpe.Altera.Map.Contracts;

using Erpe.Altera.Map.Models;

using Svg;

public interface ITracerService
{
    void AddArcs(SvgDocument document, ArcGraph arcGraph);
}
