using System.Collections.Generic;
using UnityEngine;

public class ParkingSpaceManager : MonoBehaviour
{
    [SerializeField] private List<ParkingLineTrigger> spaces;

    private Dictionary<string, bool> spaceOccupied = new Dictionary<string, bool>();

    void Start()
    {
        foreach (var s in spaces)
        {
            if (s == null) continue;
            spaceOccupied[s.spaceId] = false;
        }
    }

    public void MarkOccupied(string spaceId, bool occupied)
    {
        if (spaceOccupied.ContainsKey(spaceId))
            spaceOccupied[spaceId] = occupied;
    }

    public bool IsSpaceAvailable(string spaceId)
    {
        return spaceOccupied.TryGetValue(spaceId, out bool occ) && !occ;
    }

    public bool HasAnyAvailable()
    {
        foreach (var occ in spaceOccupied.Values)
            if (!occ) return true;
        return false;
    }

    public string LotName => gameObject.name;
}