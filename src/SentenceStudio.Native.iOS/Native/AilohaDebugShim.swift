import AilohaAgent
import Foundation

@objc(SentenceStudioAilohaDebug)
public final class SentenceStudioAilohaDebug: NSObject {
    @objc public static func start() {
        Task { @MainActor in
            do {
                try await Agent.shared.start(.init(portFallbackCount: 32))
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

}
