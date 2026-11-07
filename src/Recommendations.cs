using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Studio;

namespace GinIkThis
{
    internal sealed class Suggestion
    {
        internal string Name;
        internal Mapping Map;
        internal bool Adopt;
        internal string Error;
    }

    internal static class Recommendations
    {
        static readonly Regex Prefix = new Regex(@"^(mixamorig[:_. -]*|(?:bip\d*|bone)[ _.-]+)", RegexOptions.IgnoreCase);
        static readonly Regex Separators = new Regex(@"[\d\s_.:-]");
        static readonly string[] FrontRoot = { "frontupperleg", "foreleg", "frontleg", "frontthigh", "frontshoulder", "upperarm", "uparm", "uparma", "shoulder", "shouldera", "腕" };
        static readonly string[] FrontMid = { "frontlowerleg", "forelowerleg", "frontknee", "foreknee", "frontelbow", "lowerarm", "forearm", "forearma", "elbow", "ひじ", "肘" };
        static readonly string[] FrontTip = { "frontfoot", "forefoot", "frontpaw", "forepaw", "fronthoof", "hand", "handa", "wrist", "手首", "手" };
        static readonly string[] RearRoot = { "hindupperleg", "hindleg", "hindthigh", "rearupperleg", "rearthigh", "thigh", "thigha", "hip", "upleg", "もも", "足" };
        static readonly string[] RearMid = { "hindlowerleg", "hindknee", "rearlowerleg", "rearknee", "calf", "calfa", "knee", "lowerleg", "leg", "ひざ", "膝" };
        static readonly string[] RearTip = { "hindfoot", "hindpaw", "hindhoof", "rearfoot", "rearpaw", "foot", "foota", "ankle", "足首" };

        internal static List<Suggestion> Find(OCIItem item)
        {
            List<Suggestion> results = new List<Suggestion>();
            foreach (string side in new[] { "l", "r" })
            {
                string label = side == "l" ? "左" : "右";
                results.Add(Find(item, label + "腕・前脚", side, FrontRoot, FrontMid, FrontTip));
                results.Add(Find(item, label + "脚・後脚", side, RearRoot, RearMid, RearTip));
            }
            return results;
        }

        static int Rank(OCIChar.BoneInfo bone, string side, string[] roles)
        {
            if (!Rig.HasGuide(bone)) return -1;
            string raw = Rig.Bone(bone).name.ToLowerInvariant();
            string name = Separators.Replace(Prefix.Replace(raw, ""), "");
            string full = side == "l" ? "left" : "right";
            for (int i = 0; i < roles.Length; i++)
            {
                string role = roles[i];
                if (name == role + side || name == side + role || name == role + full || name == full + role ||
                    name == (side == "l" ? "左" : "右") + role)
                    return 200 - i * 5 - (raw.IndexOf("elbow2", StringComparison.Ordinal) >= 0 ? 30 : 0);
            }
            return -1;
        }

        static Suggestion Find(OCIItem item, string name, string side, string[] roots, string[] mids, string[] tips)
        {
            Mapping best = null;
            int bestScore = -1;
            bool tied = false;
            foreach (OCIChar.BoneInfo root in item.listBones)
            {
                int a = Rank(root, side, roots);
                if (a < 0) continue;
                foreach (OCIChar.BoneInfo mid in item.listBones)
                {
                    int b = Rank(mid, side, mids);
                    if (b < 0 || !Rig.Below(mid, root)) continue;
                    foreach (OCIChar.BoneInfo tip in item.listBones)
                    {
                        int c = Rank(tip, side, tips);
                        int score = a + b + c;
                        if (c < 0 || score < bestScore || !Rig.Below(tip, mid)) continue;
                        Mapping map = new Mapping { Name = name, Root = root, Tip = tip, Pole = true };
                        map.Middles.Add(mid);
                        try { new Rig(item, map); }
                        catch (ArgumentException) { continue; }
                        catch (InvalidOperationException) { continue; }
                        if (score == bestScore) { tied = true; continue; }
                        bestScore = score;
                        best = map;
                        tied = false;
                    }
                }
            }
            return new Suggestion { Name = name, Map = tied ? null : best, Adopt = !tied && best != null };
        }
    }
}
