import AilohaAgent
import Foundation
import UIKit

@objc(SentenceStudioAilohaDebug)
public final class SentenceStudioAilohaDebug: NSObject {
    @objc public static func start() {
        Task { @MainActor in
            do {
                try await Agent.shared.start(.debugDefault)
            } catch {
                NSLog("Native iOS Debug agent could not start: %@", String(describing: error))
            }
        }
    }

    @objc public static func stop() {
        Task { @MainActor in
            await Agent.shared.stop()
        }
    }

    @objc(tagInput:placeholder:identifier:)
    public static func tagInput(
        _ root: UIView, placeholder: String, identifier: String
    ) {
        if let input = findInput(root, placeholder: placeholder) {
            input.accessibilityIdentifier = identifier
        }
    }

    private static func findInput(_ view: UIView, placeholder: String) -> UITextField? {
        if let input = view as? UITextField, input.placeholder == placeholder {
            return input
        }
        for child in view.subviews {
            if let input = findInput(child, placeholder: placeholder) {
                return input
            }
        }
        return nil
    }
}
