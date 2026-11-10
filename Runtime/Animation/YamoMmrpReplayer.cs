using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;

namespace YAMO.UnityTools
{
    [AddComponentMenu("YAMO/YAMO MMRP Replayer")]
    [DisallowMultipleComponent]
    public sealed class YamoMmrpReplayer : MonoBehaviour
    {
        [SerializeField] private PlayableDirector director;
        [SerializeField] private List<Animator> avatars = new List<Animator>();

        public PlayableDirector Director => director;

        private void Reset()
        {
            director = GetComponent<PlayableDirector>();
        }
    }
}
