#!/usr/bin/env python3
"""Check captured Ailoha UIKit objects from the iPhone UX correction journey."""

import argparse
import json
from pathlib import Path


def all_nodes(value):
    if isinstance(value, list):
        for item in value:
            yield from all_nodes(item)
    elif isinstance(value, dict):
        yield value
        yield from all_nodes(value.get("children", []))


def one(tree, automation_id, element_type=None):
    matches = [
        node for node in all_nodes(tree)
        if node.get("automationId") == automation_id
        and (element_type is None or node.get("type") == element_type)
    ]
    assert len(matches) == 1, f"{automation_id}: expected one {element_type}, got {len(matches)}"
    return matches[0]


def frame(node):
    return node["bounds"]


def bottom(node):
    bounds = frame(node)
    return bounds["y"] + bounds["height"]


def near(actual, expected, label, tolerance=1):
    assert abs(actual - expected) <= tolerance, f"{label}: {actual} != {expected}"


def same_frame(first, second, label):
    for key in ("x", "y", "width", "height"):
        near(frame(first)[key], frame(second)[key], f"{label}.{key}")


def scroll(tree, screen_height, label):
    views = [node for node in all_nodes(tree) if node.get("fullType") == "UIScrollView"]
    assert len(views) == 1, f"{label}: expected one UIKit UIScrollView"
    near(bottom(views[0]), screen_height, f"{label} viewport bottom")
    return views[0]


def navigation(tree, title, back=False, accessibility_label=None):
    bars = [node for node in all_nodes(tree) if node.get("fullType") == "UINavigationBar"]
    assert len(bars) == 1, f"{title}: expected one native navigation bar"
    nodes = list(all_nodes(bars[0]))
    titles = [node for node in nodes if node.get("type") == "Text"
              and node.get("text") == title]
    assert len(titles) == 1, f"{title}: missing native bar title"
    near(frame(titles[0])["x"] + frame(titles[0])["width"] / 2,
         frame(bars[0])["x"] + frame(bars[0])["width"] / 2,
         f"{title} centered title", tolerance=8 if back else 2)
    assert not [node for node in all_nodes(tree)
                if node.get("automationId") == "native.quiz.back"], (
        f"{title}: custom Back button replaced UIKit"
    )
    masks = [node for node in nodes
             if node.get("fullType", "").endswith("BackButtonMaskView")]
    if back:
        assert len(masks) == 1, f"{title}: UIKit chevron is missing"
        system_back = [node for node in nodes
                       if node.get("fullType") == "_UIButtonBarButton"
                       and frame(node)["x"] < frame(titles[0])["x"]]
        assert len(system_back) == 1, f"{title}: expected one system Back item"
        hit_target = system_back[0]
        assert "Dashboard" in (accessibility_label or hit_target.get("text") or ""), (
            f"{title}: system Back accessibility does not name Dashboard"
        )
        assert not [node for node in nodes
                    if node.get("fullType") == "UIButtonLabel"
                    and node.get("text") == "Dashboard"
                    and node.get("state", {}).get("displayed")], (
            f"{title}: long Back title still consumes navigation bar width"
        )
        assert frame(hit_target)["width"] >= 44, f"{title}: Back target is too narrow"
        assert frame(hit_target)["height"] >= 44, f"{title}: Back target is too short"
        assert frame(hit_target)["x"] + frame(hit_target)["width"] <= (
            frame(titles[0])["x"] + 1), f"{title}: UIKit Back overlaps the title"
    else:
        assert not masks, f"{title}: system Back leaked onto root"


def graded_geometry(ready, graded, label):
    before = one(ready, "native.quiz.question")
    after = one(graded, "native.quiz.question")
    assert before["text"] == after["text"], f"{label}: different question"
    same_frame(before, after, f"{label} question")
    for index in range(4):
        identifier = f"native.quiz.choice.{index}"
        first = one(ready, identifier, "Button")
        second = one(graded, identifier, "Button")
        assert first["text"] == second["text"], f"{label}: option {index} text changed"
        assert first["state"]["enabled"] and not second["state"]["enabled"], (
            f"{label}: option {index} was not disabled during feedback"
        )
        same_frame(first, second, f"{label} option {index}")
        same_frame(one(ready, identifier + ".container"),
                   one(graded, identifier + ".container"),
                   f"{label} option {index} container")
    last_choice = one(graded, "native.quiz.choice.3.container")
    answer = one(graded, "native.quiz.correct-answer")
    feedback = one(graded, "native.quiz.feedback", "View")
    assert frame(answer)["y"] >= bottom(last_choice), f"{label}: answer shifted choices"
    assert frame(feedback)["y"] >= bottom(answer), f"{label}: feedback shifted choices"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    manifest = json.loads(args.evidence.read_text())
    screen_height = manifest["screenHeight"]
    screen_width = manifest["screenWidth"]
    safe_bottom = manifest["safeBottom"]
    assert screen_height > screen_width > 0 and safe_bottom > 0
    accessibility_label = None
    if "nativeBackAxLog" in manifest:
        ax_log = (args.evidence.parent / manifest["nativeBackAxLog"]).read_text()
        assert "NAV_V3_BACK_AX label=Dashboard frame=" in ax_log
        assert ("Test Case '-[NativeBackUITests.NativeBackUITests "
                "test01SystemBackAccessibilityAndTap]' passed") in ax_log
        accessibility_label = "Dashboard"

    def load(name):
        path = args.evidence.parent / manifest["trees"][name]
        return json.loads(path.read_text())

    dashboard = load("dashboard")
    dashboard_end = load("dashboardEnd")
    expanded = load("insightExpanded")
    ready = load("quizReady")
    native_ready = load("nativeBackReady")
    resume_choice = load("resumeChoice")
    correct = load("quizCorrect")
    wrong_ready = load("quizWrongReady")
    wrong = load("quizWrong")
    text_ready = load("textReady")
    correction = load("textCorrection")
    cold_feedback = load("textColdFeedback")
    summary_nav = load("summaryNav")
    summary_end = load("summaryEnd")
    after_back = load("afterBack")
    after_complete = load("afterComplete")
    sign_in = load("signIn")
    authenticated = load("authenticated")
    cold_restore = load("coldRestore")
    large_text = load("largeText")

    navigation(dashboard, "Dashboard")
    for tree in (native_ready, resume_choice, text_ready, correction,
                 cold_feedback, summary_nav):
        navigation(tree, "Vocabulary Quiz", back=True,
                   accessibility_label=accessibility_label)
    navigation(after_back, "Dashboard")
    navigation(after_complete, "Dashboard")
    for label, tree in (("Dashboard", dashboard), ("Dashboard bottom", dashboard_end),
                        ("summary", summary_end), ("sign-in", sign_in)):
        scroll(tree, screen_height, label)
    assert bottom(one(dashboard_end, "native.dashboard.vocab.summary")) <= (
        screen_height - safe_bottom + 1), "Dashboard final content hits home indicator"
    assert bottom(one(summary_end, "native.quiz.done")) <= (
        screen_height - safe_bottom + 1), "Summary Done hits home indicator"
    near(bottom(one(native_ready, "native.quiz.footer")),
         screen_height, "quiz footer bottom")

    card = one(dashboard, "native.plan.insight")
    header = one(dashboard, "native.plan.insight.toggle", "Button")
    assert header["role"] == "button" and header["state"]["enabled"]
    same_frame(card, header, "collapsed full-card hit target")
    expanded_card = one(expanded, "native.plan.insight")
    expanded_header = one(expanded, "native.plan.insight.toggle", "Button")
    near(frame(expanded_header)["x"], frame(expanded_card)["x"], "expanded header left")
    near(frame(expanded_header)["width"], frame(expanded_card)["width"],
         "expanded header width")
    assert frame(one(expanded, "native.plan.insight.story"))["y"] >= bottom(expanded_header)
    for child in all_nodes(expanded_card.get("children", [])):
        if child.get("role") in ("button", "link") and child is not expanded_header:
            assert frame(child)["y"] >= bottom(expanded_header), (
                "An expanded-content action overlaps the accordion button"
            )

    graded_geometry(ready, correct, "correct")
    graded_geometry(wrong_ready, wrong, "incorrect")
    same_frame(one(text_ready, "native.quiz.question"),
               one(correction, "native.quiz.question"), "text question")
    same_frame(one(text_ready, "native.quiz.answer"),
               one(correction, "native.quiz.answer"), "text input")
    ready_question = one(text_ready, "native.quiz.question")
    cold_question = one(cold_feedback, "native.quiz.question")
    assert ready_question["text"] == cold_question["text"]
    pending_offset = frame(cold_question)["y"] - frame(ready_question)["y"]
    if [node for node in all_nodes(cold_feedback)
            if node.get("automationId") == "native.quiz.pending"]:
        assert pending_offset > 0, "Pending-sync banner did not shift the content"
    for identifier in ("native.quiz.question", "native.quiz.answer"):
        before = frame(one(text_ready, identifier))
        after = frame(one(cold_feedback, identifier))
        for key in ("x", "width", "height"):
            near(after[key], before[key], f"cold-resumed {identifier}.{key}")
        near(after["y"] - before["y"], pending_offset,
             f"cold-resumed {identifier} pending offset")
    assert one(correction, "native.quiz.answer")["value"] == (
        one(cold_feedback, "native.quiz.answer")["value"])
    assert not one(cold_feedback, "native.quiz.answer")["state"]["enabled"]
    assert one(cold_feedback, "native.quiz.answer.correction")["state"]["enabled"]
    assert frame(one(correction, "native.quiz.feedback", "View"))["y"] >= (
        bottom(one(correction, "native.quiz.answer")))
    assert frame(one(correction, "native.quiz.answer.correction"))["y"] >= (
        bottom(one(correction, "native.quiz.feedback", "View")))

    assert not [node for node in all_nodes(sign_in)
                if node.get("fullType") == "UINavigationBar"], "Sign-in showed a stale bar"
    assert one(sign_in, "native.auth.email")["type"] == "TextInput"
    assert one(sign_in, "native.auth.password")["type"] == "TextInput"
    for label, tree in (("authenticated", authenticated),
                        ("cold restore", cold_restore)):
        navigation(tree, "Dashboard")
        assert not [node for node in all_nodes(tree)
                    if node.get("automationId") == "native.auth.submit"], (
            f"{label}: stacked a sign-in root behind Dashboard"
        )
    assert not [node for node in all_nodes(after_back)
                if node.get("automationId") == "native.quiz.question"]
    assert one(after_complete, "native.plan.card.completed")["text"]
    scroll(large_text, screen_height, "Dynamic Type Dashboard")
    assert frame(one(large_text, "native.plan.insight.toggle"))["height"] > (
        frame(header)["height"]), "Large text did not grow the insight header"
    print("Native UIKit system Back, inset, quiz geometry, insight, auth and Dynamic Type contracts passed")


if __name__ == "__main__":
    main()
