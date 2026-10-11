"""Selection regressions; run: python -m unittest discover -s port -p test_variant_selection.py."""
import unittest

import variant_selection as audit


class SelectionTests(unittest.TestCase):
    def program(self):
        variants = [dict(subShader=0, passName="FORWARD", **{"pass": 0}, stage=stage, keywords=keys)
                    for stage in ("vs", "ps") for keys in (["A", "POINT"], ["A", "SPOT"])]
        text = '''Shader "Test" {
SubShader {
Pass {
Tags { "LightMode"="ForwardAdd" }
CGPROGRAM
#pragma multi_compile A
#pragma multi_compile POINT SPOT
#if A && POINT // FORWARD:DX11VertexSM40
// CBs for DX11VertexSM40
#if !UNITY_UV_STARTS_AT_TOP
#else
#endif
#else
// CBs for DX11VertexSM40
#endif
#if A && POINT // FORWARD:DX11PixelSM40
// CBs for DX11PixelSM40
#else
// CBs for DX11PixelSM40
#endif
ENDCG
}
}
}'''
        return audit.parse_programs(text, variants)[0], text, variants

    def test_nested_platform_else_is_not_a_variant(self):
        p, _, _ = self.program()
        self.assertEqual([len(p.branches[s]) for s in ("vs", "ps")], [2, 2])
        self.assertEqual(audit.compare(p, "vs", {"POINT"})["reason"], "match")
        self.assertEqual(audit.compare(p, "ps", {"SPOT"})["branch"], 4)

    def test_dump_order_is_validated(self):
        _, text, variants = self.program()
        variants[0]["keywords"] = ["A", "SPOT"]
        with self.assertRaisesRegex(ValueError, "Dump/body order"):
            audit.parse_programs(text, variants)

    def test_unknown_original_combination_is_not_guessed(self):
        p, _, _ = self.program()
        p.variants["vs"] = [dict(id=1, keywords=["A", "POINT"]), dict(id=2, keywords=["B", "SPOT"])]
        self.assertEqual(audit.compare(p, "vs", {"A", "SPOT"})["reason"], "no-exact-original")

    def test_wrong_fallback_is_detected(self):
        p, _, _ = self.program()
        p.groups = [("multi_compile", ["A"]), ("multi_compile", ["DIRECTIONAL"])]
        r = audit.compare(p, "vs", {"POINT"})
        self.assertEqual(r["reason"], "wrong-fallback")
        self.assertEqual(r["expected"], [1])
        self.assertEqual(r["branch"], 2)

    def test_stage_irrelevant_keyword_is_ignored(self):
        p, _, _ = self.program()
        self.assertEqual(audit.compare(p, "ps", {"POINT", "VERTEXLIGHT_ON"})["reason"], "match")

    def test_earlier_subset_branch_cannot_shadow_a_later_original(self):
        p, _, _ = self.program()
        p.variants["vs"] = [dict(id=1, keywords=["A"]), dict(id=2, keywords=["A", "B"])]
        p.branches["vs"] = [dict(id=1, keywords=["A"], condition="A"),
                            dict(id=2, keywords=["A", "B"], condition=None)]
        p.groups = [("multi_compile", ["A"]), ("shader_feature", [None, "B"])]
        self.assertEqual(audit.compare(p, "vs", {"B"})["reason"], "wrong-conditional-branch")

    def test_conflict_does_not_claim_empirical_selection(self):
        p, _, _ = self.program()
        self.assertEqual(audit.compare(p, "ps", {"POINT", "SPOT"})["reason"], "ambiguous-original-request")

    def test_pragma_defaults_and_single_feature(self):
        groups = audit.pragma_groups("#pragma multi_compile OFF ON\n#pragma shader_feature PIXELSNAP_ON\n")
        self.assertEqual(audit.compiled_keywords(groups, set())[0], {"OFF"})
        self.assertEqual(audit.compiled_keywords(groups, {"ON", "PIXELSNAP_ON"})[0], {"ON", "PIXELSNAP_ON"})

    def test_instrumentation_preserves_conditions_and_separates_stages(self):
        p, _, _ = self.program()
        source, keys = audit.instrument(p, "Hidden/Test")
        self.assertIn("#pragma multi_compile POINT SPOT", source)
        self.assertEqual(source.count("#if A && POINT"), 2)
        self.assertIn("#define ETG_V 2", source)
        self.assertIn("#define ETG_P 4", source)
        self.assertIn("ColorMask 0", source)
        self.assertIn("POINT", keys)

    def test_unsupported_pragmas_fail_closed(self):
        with self.assertRaisesRegex(ValueError, "Unsupported pragma"):
            audit.pragma_groups("#pragma multi_compile_fwdbase\n")


if __name__ == "__main__":
    unittest.main()
