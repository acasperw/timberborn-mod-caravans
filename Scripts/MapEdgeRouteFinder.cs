using System.Collections.Generic;
using Timberborn.Navigation;
using Timberborn.ZiplineSystem;
using UnityEngine;

namespace Mods.Caravans.Scripts {
  // Finds the road route from a building's access to the nearest road tile on the map border. Posts only need to
  // search again when RoadsVersion changes, which happens whenever the game updates the road network.
  public class MapEdgeRouteFinder : ISingletonNavMeshListener {

    private readonly RoadNavMeshGraph _roadNavMeshGraph;
    private readonly NodeIdService _nodeIdService;
    private readonly INavMeshSizeProvider _navMeshSizeProvider;
    private readonly PathfindingService _pathfindingService;
    private readonly ZiplineGroupService _ziplineGroupService;
    private readonly Queue<int> _toVisit = new Queue<int>();
    private readonly HashSet<int> _visited = new HashSet<int>();
    private readonly List<int> _destination = new List<int>(1);
    private readonly List<PathCorner> _corners = new List<PathCorner>();
    private readonly List<CaravanRoute.Corner> _routeCorners = new List<CaravanRoute.Corner>();

    public MapEdgeRouteFinder(RoadNavMeshGraph roadNavMeshGraph,
                              NodeIdService nodeIdService,
                              INavMeshSizeProvider navMeshSizeProvider,
                              PathfindingService pathfindingService,
                              ZiplineGroupService ziplineGroupService) {
      _roadNavMeshGraph = roadNavMeshGraph;
      _nodeIdService = nodeIdService;
      _navMeshSizeProvider = navMeshSizeProvider;
      _pathfindingService = pathfindingService;
      _ziplineGroupService = ziplineGroupService;
    }

    public int RoadsVersion { get; private set; }

    public void OnNavMeshUpdated(NavMeshUpdate navMeshUpdate) {
      if (navMeshUpdate.UpdatedRoads) {
        RoadsVersion++;
      }
    }

    // The game's own road path (weighted like the beavers' routes, with corners placed for stairs and zipline cables),
    // or null.
    public CaravanRoute FindRoute(Vector3 accessPosition) {
      if (!TryFindMapEdgeNode(accessPosition, out var edgeNodeId)) {
        return null;
      }
      _destination.Clear();
      _destination.Add(edgeNodeId);
      _corners.Clear();
      if (!_pathfindingService.FindRoadPathUncached(accessPosition, _destination, out _, _corners)) {
        return null;
      }
      _routeCorners.Clear();
      foreach (var corner in _corners) {
        _routeCorners.Add(new CaravanRoute.Corner(corner.Position, corner.Speed,
                                                  _ziplineGroupService.IsAnyZiplineGroup(corner.GroupId)));
      }
      return CaravanRoute.Create(_routeCorners);
    }

    // Breadth-first search over the roads for the closest node on the map border.
    private bool TryFindMapEdgeNode(Vector3 accessPosition, out int edgeNodeId) {
      edgeNodeId = -1;
      if (!_nodeIdService.Contains(accessPosition)) {
        return false;
      }
      var size = _navMeshSizeProvider.Size;
      _toVisit.Clear();
      _visited.Clear();
      var start = _nodeIdService.WorldToId(accessPosition);
      _toVisit.Enqueue(start);
      _visited.Add(start);
      while (_toVisit.Count > 0) {
        var nodeId = _toVisit.Dequeue();
        var grid = _nodeIdService.IdToGrid(nodeId);
        if (grid.x <= 0 || grid.y <= 0 || grid.x >= size.x - 1 || grid.y >= size.y - 1) {
          edgeNodeId = nodeId;
          return true;
        }
        var neighbors = _roadNavMeshGraph.GetNeighbors(nodeId);
        for (var i = 0; i < neighbors.Count; i++) {
          if (_visited.Add(neighbors[i].Id)) {
            _toVisit.Enqueue(neighbors[i].Id);
          }
        }
      }
      return false;
    }

  }
}