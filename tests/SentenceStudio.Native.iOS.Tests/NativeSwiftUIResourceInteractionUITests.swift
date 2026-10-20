import XCTest

final class NativeSwiftUIResourceInteractionUITests: XCTestCase {
    private let bundleId = "com.simplyprofound.sentencestudio.native.debug"
    private let smartId = "4eb31e7d-c264-4a6d-98d6-996d9001a1bd"
    private let editableId = "1751240f-d00e-4136-8bd0-042cb2c3ba9d"

    override func setUp() {
        super.setUp()
        continueAfterFailure = false
    }

    func testGridSmartCardContentAndEdgeOpenReadOnlySwiftUIForm() {
        verifyWholeCard(mode: "grid", resourceId: smartId, smart: true,
                        taps: [CGVector(dx: 0.40, dy: 0.30),
                               CGVector(dx: 0.92, dy: 0.78)])
    }

    func testGridEditableIconAndMetadataOpenFullSwiftUIForm() {
        verifyWholeCard(mode: "grid", resourceId: editableId, smart: false,
                        taps: [CGVector(dx: 0.08, dy: 0.35),
                               CGVector(dx: 0.92, dy: 0.78)])
    }

    func testListSmartRowOpensOneReadOnlySwiftUIForm() {
        verifyWholeCard(mode: "list", resourceId: smartId, smart: true,
                        taps: [CGVector(dx: 0.50, dy: 0.50)])
    }

    func testListEditableRowOpensOneFullSwiftUIForm() {
        verifyWholeCard(mode: "list", resourceId: editableId, smart: false,
                        taps: [CGVector(dx: 0.92, dy: 0.50)])
    }

    func testFocusedQuickAddSearchAndEscapeRetainFullForm() {
        let app = XCUIApplication(bundleIdentifier: bundleId)
        app.launch()
        openResources(app, mode: "list")
        let row = resourceRow(editableId, in: app)
        scrollIntoView(row, app: app)
        row.tap()

        let query = app.textFields["native.resources.edit.quick.search"]
        for _ in 0..<12 {
            if query.isHittable { break }
            app.swipeUp()
        }
        XCTAssertTrue(query.isHittable)
        XCTAssertTrue(query.isEnabled)
        query.tap()
        query.typeText("go")
        let options = app.buttons.matching(NSPredicate(
            format: "identifier BEGINSWITH %@", "native.resources.edit.quick.option."))
        XCTAssertTrue(options.firstMatch.waitForExistence(timeout: 15))
        capture(app, "quick-add-before-escape")
        query.typeKey(XCUIKeyboardKey.escape, modifierFlags: [])
        XCTAssertNotEqual(query.value as? String, "go")
        XCTAssertFalse(options.firstMatch.exists)
        XCTAssertFalse(app.buttons["native.resources.edit.quick.new"].exists)
        capture(app, "quick-add-escape-cleared")

        query.typeText("go")
        XCTAssertTrue(options.firstMatch.waitForExistence(timeout: 15))
        query.typeKey(XCUIKeyboardKey.escape, modifierFlags: [])
        XCTAssertFalse(options.firstMatch.exists)
        XCTAssertTrue(app.textFields["native.resources.edit.title"].exists)
        app.navigationBars.buttons["BackButton"].tap()
        XCTAssertTrue(app.buttons["Add Resource"].waitForExistence(timeout: 15))
    }

    func testFilteredZeroUsesSourceEmptyActionsWithoutPretendingThereAreRows() {
        let app = XCUIApplication(bundleIdentifier: bundleId)
        app.launch()
        openResources(app, mode: "grid")
        let search = app.textFields["native.resources.search"]
        XCTAssertTrue(search.isHittable)
        search.tap()
        search.typeText("resource_nonmatch_20261005")
        let empty = app.staticTexts["native.resources.status"]
        XCTAssertTrue(empty.waitForExistence(timeout: 15))
        XCTAssertEqual(empty.label, "No resources found")
        XCTAssertTrue(app.buttons["native.resources.empty.add"].exists)
        XCTAssertTrue(app.buttons["native.resources.empty.starter"].exists)
        XCTAssertFalse(app.staticTexts["native.resources.count"].exists)
        capture(app, "resources-filtered-zero-actions")
    }

    func testDeleteCancelKeepsProtectedEditableResourceAndDraft() {
        let app = XCUIApplication(bundleIdentifier: bundleId)
        app.launch()
        openResources(app, mode: "list")
        let row = resourceRow(editableId, in: app)
        scrollIntoView(row, app: app)
        row.tap()
        let form = app.descendants(matching: .any)
            .matching(identifier: "native.resources.edit.screen").firstMatch
        XCTAssertTrue(form.waitForExistence(timeout: 15))
        app.buttons["native.resources.form.more"].tap()
        app.buttons["native.resources.form.delete.menu"].tap()
        let cancel = app.buttons["native.resources.edit.delete.cancel"]
        XCTAssertTrue(cancel.waitForExistence(timeout: 15))
        capture(app, "resources-delete-confirm-cancel-only")
        cancel.tap()
        XCTAssertTrue(form.exists)
        XCTAssertTrue(app.textFields["native.resources.edit.title"].exists)
        app.navigationBars.buttons["BackButton"].tap()
        XCTAssertTrue(resourceRow(editableId, in: app).exists)
    }

    func testHardwareArrowAndReturnOpenCreateSuggestionWithoutWritingProtectedRows() {
        let app = XCUIApplication(bundleIdentifier: bundleId)
        app.launch()
        openResources(app, mode: "list")
        let row = resourceRow(editableId, in: app)
        scrollIntoView(row, app: app)
        row.tap()

        let query = app.textFields["native.resources.edit.quick.search"]
        for _ in 0..<12 {
            if query.isHittable { break }
            app.swipeUp()
        }
        XCTAssertTrue(query.isHittable)
        query.tap()
        query.typeText("quickadd_new_word_20261005")
        let createOption = app.buttons["native.resources.edit.quick.new"]
        XCTAssertTrue(createOption.waitForExistence(timeout: 15))
        XCTAssertTrue(createOption.isSelected)
        query.typeKey(.downArrow, modifierFlags: [])
        XCTAssertTrue(createOption.isSelected)
        query.typeKey(.upArrow, modifierFlags: [])
        XCTAssertTrue(createOption.isSelected)
        capture(app, "quick-add-selected-create-before-enter")
        query.typeKey(.return, modifierFlags: [])
        XCTAssertTrue(app.textFields["native.resources.edit.quick.native"]
            .waitForExistence(timeout: 15))
        XCTAssertFalse(createOption.exists)
        capture(app, "quick-add-enter-opened-unsaved-create")
        app.navigationBars.buttons["BackButton"].tap()
        XCTAssertTrue(resourceRow(editableId, in: app).exists)
    }

    func testBlankNewTitleReportsValidationWithoutLeavingEditableDraft() {
        let app = XCUIApplication(bundleIdentifier: bundleId)
        app.launch()
        openResources(app, mode: "grid")
        app.buttons["native.resources.more"].tap()
        app.buttons["native.resources.add"].tap()

        let form = app.descendants(matching: .any)
            .matching(identifier: "native.resources.add.screen").firstMatch
        XCTAssertTrue(form.waitForExistence(timeout: 15))
        XCTAssertTrue(app.textFields["native.resources.edit.title"].exists)
        app.buttons["native.resources.form.more"].tap()
        app.buttons["native.resources.edit.save"].tap()
        XCTAssertTrue(app.staticTexts["native.resources.form.error"]
            .waitForExistence(timeout: 15))
        XCTAssertTrue(form.exists, "A blank title must not discard the unsaved draft")
        XCTAssertTrue(app.textFields["native.resources.edit.title"].exists)
        capture(app, "add-blank-title-validation")
    }

    private func verifyWholeCard(
        mode: String, resourceId: String, smart: Bool,
        taps: [CGVector]
    ) {
        let app = XCUIApplication(bundleIdentifier: bundleId)
        app.launch()
        openResources(app, mode: mode)

        for point in taps {
            let row = resourceRow(resourceId, in: app)
            scrollIntoView(row, app: app)
            let cardLabel = row.label
            capture(app, "\(mode)-\(smart ? "smart" : "editable")-before-tap")
            row.coordinate(withNormalizedOffset: point).tap()

            let form = app.descendants(matching: .any)
                .matching(identifier: "native.resources.edit.screen").firstMatch
            XCTAssertTrue(form.waitForExistence(timeout: 15))
            let smartNotice = app.staticTexts["native.resources.form.readonly"]
            let more = app.buttons["native.resources.form.more"]
            if smart {
                XCTAssertTrue(smartNotice.exists)
                XCTAssertFalse(app.textFields["native.resources.edit.title"].exists)
                XCTAssertFalse(more.exists)
            } else {
                let title = app.textFields["native.resources.edit.title"]
                XCTAssertTrue(title.waitForExistence(timeout: 15))
                XCTAssertTrue(cardLabel.contains(title.value as? String ?? ""))
                XCTAssertFalse(smartNotice.exists)
                XCTAssertTrue(more.exists)
                XCTAssertTrue(app.buttons["native.resources.form.media.type"].exists)
                XCTAssertTrue(app.buttons["native.resources.form.language"].exists)
            }
            capture(app, "\(mode)-\(smart ? "smart" : "editable")-full-form")

            let back = app.navigationBars.buttons["BackButton"]
            XCTAssertTrue(back.exists)
            back.tap()
            XCTAssertTrue(app.buttons["Add Resource"].waitForExistence(timeout: 15))
            XCTAssertFalse(form.exists, "One native Back must undo one whole-card activation")
        }
    }

    private func openResources(_ app: XCUIApplication, mode: String) {
        let tab = app.tabBars.buttons["Learning Resources"]
        XCTAssertTrue(tab.waitForExistence(timeout: 45))
        tab.tap()
        let modes = app.segmentedControls["native.resources.view.mode"]
        XCTAssertTrue(modes.waitForExistence(timeout: 15))
        let choice = modes.buttons[mode == "grid" ? "Grid view" : "List view"]
        XCTAssertTrue(choice.exists)
        choice.tap()
        XCTAssertTrue(app.buttons["native.resources.add"].exists,
                      "Scrolling the list must keep the header control's own AX ID")
        XCTAssertTrue(app.buttons["Add Resource"].waitForExistence(timeout: 15))
    }

    private func resourceRow(_ id: String, in app: XCUIApplication) -> XCUIElement {
        app.buttons["native.resources.row.\(id)"]
    }

    private func scrollIntoView(_ row: XCUIElement, app: XCUIApplication) {
        for _ in 0..<16 {
            if row.exists && row.isHittable {
                let frame = row.frame
                if frame.width >= 44 && frame.height >= 44
                    && frame.maxY < app.tabBars.firstMatch.frame.minY - 8 {
                    return
                }
            }
            app.swipeUp()
        }
        XCTFail("The owned resource card must be fully hittable above native tabs")
    }

    private func capture(_ app: XCUIApplication, _ name: String) {
        let screenshot = XCTAttachment(screenshot: app.screenshot())
        screenshot.name = name
        screenshot.lifetime = .keepAlways
        add(screenshot)
    }
}
