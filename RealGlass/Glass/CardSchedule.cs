using System.Collections.Generic;

namespace RealGlass.Glass
{
    /// <summary>
    /// Which building assets get a facade card, where, and when (FacadeCardsGame; a trial, 2026-11-13).
    /// Pure: no game or engine types, tested offline (tools\test-cards.ps1).
    /// </summary>
    /// <remarks>
    /// <para>Two kinds of slot: FULL ones (sharp, for the nearest assets) and SMALL ones (a quarter of the
    /// room, for the rest). Each new city map hands in the assets standing in it with their nearest
    /// building's distance to the camera (<see cref="SetStanding"/>); the nearest FullCount of them want a
    /// full slot, the others a small one. <see cref="Next"/> hands out bakes nearest first, up to the
    /// caller's budget. A slot is taken, in this order: a free one; one whose asset does not stand in the
    /// map (least recently seen first); one held by a standing asset FARTHER than the one asking (the
    /// farthest first) -- so near buildings always get a card before far ones. An asset holding a small
    /// slot that comes near moves to a full one (its small one freed); one holding a full slot that goes
    /// far keeps it until a nearer asset needs it. An asset that could not be baked is not tried again.</para>
    /// <para>No allocation per call once warm (the caller runs it every frame).</para>
    /// </remarks>
    public sealed class CardSchedule
    {
        public struct Job
        {
            public string Key;
            public int Slot;
        }

        private readonly int _full, _capacity;
        private readonly Dictionary<string, int> _slotOf = new Dictionary<string, int>();
        private readonly string[] _keyIn;          // per slot (1-based; [0] unused)
        private readonly int[] _seen;              // per slot: the last map it stood in
        private readonly HashSet<string> _failed = new HashSet<string>();
        private readonly List<string> _standing = new List<string>();
        private readonly Dictionary<string, int> _rank = new Dictionary<string, int>();   // 0 = nearest
        private readonly List<float> _distance = new List<float>();
        private readonly List<int> _order = new List<int>();
        private readonly List<Job> _jobs = new List<Job>();
        private readonly System.Comparison<int> _byDistance;
        private int _map;
        private int _waiting;
        private bool _waitingDirty = true;

        /// <summary>One kind of slot (all full).</summary>
        public CardSchedule(int capacity) : this(capacity, 0)
        {
        }

        /// <summary>full sharp slots (1..full) and small ones after them (full+1..full+small).</summary>
        public CardSchedule(int full, int small)
        {
            _full = full < 0 ? 0 : full;
            _capacity = _full + (small < 0 ? 0 : small);
            _keyIn = new string[_capacity + 1];
            _seen = new int[_capacity + 1];
            _byDistance = (a, b) => _distance[a].CompareTo(_distance[b]);
        }

        public int Capacity
        {
            get { return _capacity; }
        }

        public int FullCount
        {
            get { return _full; }
        }

        /// <summary>Slots holding a card.</summary>
        public int Used
        {
            get { return _slotOf.Count; }
        }

        /// <summary>Assets standing in the current map without a card (not counting those that failed).</summary>
        /// <remarks>Counted again only after something changed (a map, a card handed out or freed, a failure,
        /// a Clear): the caller asks every frame, and a full count is two look-ups per standing asset.</remarks>
        public int Waiting
        {
            get
            {
                if (_waitingDirty)
                {
                    int n = 0;
                    for (int i = 0; i < _standing.Count; i++)
                    {
                        if (!_slotOf.ContainsKey(_standing[i]) && !_failed.Contains(_standing[i]))
                            n++;
                    }
                    _waiting = n;
                    _waitingDirty = false;
                }
                return _waiting;
            }
        }

        public int Failed
        {
            get { return _failed.Count; }
        }

        /// <summary>The asset's slot (1..capacity), 0 for none.</summary>
        public int SlotOf(string key)
        {
            int slot;
            return key != null && _slotOf.TryGetValue(key, out slot) ? slot : 0;
        }

        public bool IsFull(int slot)
        {
            return slot >= 1 && slot <= _full;
        }

        /// <summary>A new city map's assets (keys; duplicates count once) and each one's nearest distance to the camera.</summary>
        public void SetStanding(IList<string> keys, IList<float> distances)
        {
            _map++;
            _waitingDirty = true;
            _standing.Clear();
            _distance.Clear();
            _rank.Clear();
            for (int i = 0; i < keys.Count; i++)
            {
                string key = keys[i];
                if (key == null || _rank.ContainsKey(key))
                    continue;
                _rank[key] = -1;
                _standing.Add(key);
                _distance.Add(i < distances.Count ? distances[i] : float.MaxValue);
            }
            _order.Clear();
            for (int i = 0; i < _standing.Count; i++)
                _order.Add(i);
            _order.Sort(_byDistance);
            for (int r = 0; r < _order.Count; r++)
            {
                string key = _standing[_order[r]];
                _rank[key] = r;
                int slot;
                if (_slotOf.TryGetValue(key, out slot))
                    _seen[slot] = _map;
            }
        }

        /// <summary>
        /// Up to maxJobs bakes for this frame, nearest first (the list is reused: read it before the next
        /// call). The caller bakes each and calls <see cref="Done"/> (false when the asset could not be
        /// baked: its slot is freed and it is not tried again).
        /// </summary>
        public List<Job> Next(int maxJobs)
        {
            _jobs.Clear();
            if (maxJobs <= 0 || _capacity == 0)
                return _jobs;

            for (int r = 0; r < _order.Count && _jobs.Count < maxJobs; r++)
            {
                string key = _standing[_order[r]];
                if (_failed.Contains(key))
                    continue;
                int has = SlotOf(key);
                bool wantsFull = _full > 0 && r < _full;
                if (has != 0 && (IsFull(has) || !wantsFull))
                    continue;   // a card of the kind it wants, or a full one it may keep

                int slot = wantsFull ? Take(1, _full, r) : 0;
                if (slot == 0 && has == 0)
                    slot = Take(_full + 1, _capacity, r);
                if (slot == 0 && has == 0 && _full > 0 && !wantsFull)
                    slot = Take(1, _full, r);   // no small room left: a full slot held by a farther asset
                if (slot == 0)
                {
                    // An asset with no card that found none has asked every slot (full and small): each
                    // is then held by a standing asset NEARER than it. So no farther asset holds a card
                    // to upgrade or can take one either: the rest of the list would change nothing.
                    // Stopping here keeps a map with more assets than slots from scanning every slot
                    // twice for each asset past the last one, every frame (at 4,000 assets 56-131 ms a
                    // frame offline before, under 0.1 ms since: tools\test-cards.ps1 times both).
                    if (has == 0)
                        break;
                    continue;
                }
                if (has != 0)
                    Release(has);
                _keyIn[slot] = key;
                _slotOf[key] = slot;
                _seen[slot] = _map;
                _jobs.Add(new Job { Key = key, Slot = slot });
            }
            // Every change Next makes (a card handed out, one taken from another asset) comes with a job.
            if (_jobs.Count > 0)
                _waitingDirty = true;
            return _jobs;
        }

        /// <summary>A bake handed out by <see cref="Next"/> finished: false frees its slot and never tries the asset again.</summary>
        public void Done(Job job, bool ok)
        {
            if (ok)
                return;
            _waitingDirty = true;
            _failed.Add(job.Key);
            if (job.Slot > 0 && job.Slot <= _capacity && _keyIn[job.Slot] == job.Key)
                Release(job.Slot);
        }

        private void Release(int slot)
        {
            string key = _keyIn[slot];
            if (key != null)
                _slotOf.Remove(key);
            _keyIn[slot] = null;
        }

        // A slot in [lo, hi] for the asset of rank r: free, else not standing (least recently seen), else
        // standing farther than r (the farthest); 0: none. A slot taken from another asset is released.
        private int Take(int lo, int hi, int r)
        {
            int idle = 0, idleSeen = int.MaxValue, far = 0, farRank = r;
            for (int s = lo; s <= hi; s++)
            {
                string key = _keyIn[s];
                if (key == null)
                    return s;
                int rank;
                if (!_rank.TryGetValue(key, out rank))
                {
                    if (_seen[s] < idleSeen)
                    {
                        idleSeen = _seen[s];
                        idle = s;
                    }
                }
                else if (rank > farRank)
                {
                    farRank = rank;
                    far = s;
                }
            }
            int take = idle != 0 ? idle : far;
            if (take != 0)
                Release(take);
            return take;
        }

        /// <summary>Every card forgotten (the atlases freed).</summary>
        public void Clear()
        {
            _waitingDirty = true;
            _slotOf.Clear();
            for (int s = 0; s <= _capacity; s++)
            {
                _keyIn[s] = null;
                _seen[s] = 0;
            }
        }
    }
}
