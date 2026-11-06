using System.Collections.Generic;
using Timberborn.Navigation;
using UnityEngine;

namespace Mods.Caravans.Scripts {
  // Walks the road graph from a building's access tile to the nearest road tile on the map border.
  public class MapEdgeRouteFinder {

    private readonly RoadNavMeshGraph _roadNavMeshGraph;
    private readonly NodeIdService _nodeIdService;
    private readonly INavMeshSizeProvider _navMeshSizeProvider;
    private readonly Queue<int> _toVisit = new Queue<int>();
    private readonly Dictionary<int, int> _parents = new Dictionary<int, int>();

    public MapEdgeRouteFinder(RoadNavMeshGraph roadNavMeshGraph,
                              NodeIdService nodeIdService,
                              INavMeshSizeProvider navMeshSizeProvider) {
      _roadNavMeshGraph = roadNavMeshGraph;
      _nodeIdService = nodeIdService;
      _navMeshSizeProvider = navMeshSizeProvider;
    }

    public bool HasRoadToMapEdge(Vector3 accessPosition) {
      return TryFindRoadToMapEdge(accessPosition, null);
    }

    public bool TryFindRoadToMapEdge(Vector3 accessPosition, List<Vector3> path) {
      if (!_nodeIdService.Contains(accessPosition)) {
        return false;
      }
      var size = _navMeshSizeProvider.Size;
      _toVisit.Clear();
      _parents.Clear();
      var start = _nodeIdService.WorldToId(accessPosition);
      _toVisit.Enqueue(start);
      _parents[start] = -1;
      while (_toVisit.Count > 0) {
        var nodeId = _toVisit.Dequeue();
        var grid = _nodeIdService.IdToGrid(nodeId);
        if (grid.x <= 0 || grid.y <= 0 || grid.x >= size.x - 1 || grid.y >= size.y - 1) {
          if (path != null) {
            BuildPath(nodeId, path);
          }
          return true;
        }
        var neighbors = _roadNavMeshGraph.GetNeighbors(nodeId);
        for (var i = 0; i < neighbors.Count; i++) {
          var neighborId = neighbors[i].Id;
          if (!_parents.ContainsKey(neighborId)) {
            _parents[neighborId] = nodeId;
            _toVisit.Enqueue(neighborId);
          }
        }
      }
      return false;
    }

    private void BuildPath(int endNodeId, List<Vector3> path) {
      path.Clear();
      for (var id = endNodeId; id != -1; id = _parents[id]) {
        path.Add(_nodeIdService.IdToWorld(id));
      }
      path.Reverse();
    }

  }
}
