import AppKit

/// A button in the bubble. `run` returns the confirmation to show, or nil to just close it.
struct ToastAction {
    let title: String
    let run: () -> String?
}

/// The small bubble at the top of the screen ("Copied text"), optionally with quick-action
/// buttons. Fades out on its own; stays while the pointer is over it.
final class Toast {
    private static var panel: NSPanel?
    private static var hideWork: DispatchWorkItem?
    private static var targets: [ActionTarget] = []
    private static var clipboardTimer: Timer?
    private static var deadline = Date()

    /// How long the bubble stays when it has quick actions (unless you paste first).
    static let actionsLifetime: TimeInterval = 3

    static func show(_ message: String, symbol: String = "doc.on.clipboard", actions: [ToastAction] = []) {
        hideWork?.cancel()
        panel?.orderOut(nil)
        targets = []
        stopWatchingForPaste()

        let icon = NSImageView(image: NSImage(systemSymbolName: symbol, accessibilityDescription: nil) ?? NSImage())
        icon.contentTintColor = .labelColor
        icon.symbolConfiguration = .init(pointSize: 13, weight: .semibold)

        let label = NSTextField(labelWithString: message)
        label.font = .systemFont(ofSize: 13, weight: .semibold)
        label.textColor = .labelColor
        label.lineBreakMode = .byTruncatingTail
        label.maximumNumberOfLines = 1

        var views: [NSView] = [icon, label]
        if !actions.isEmpty {
            let divider = NSBox()
            divider.boxType = .separator
            divider.translatesAutoresizingMaskIntoConstraints = false
            divider.heightAnchor.constraint(equalToConstant: 16).isActive = true
            views.append(divider)
            for action in actions {
                let target = ActionTarget(action)
                targets.append(target)
                views.append(pillButton(action.title, target: target))
            }
        }

        let stack = NSStackView(views: views)
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 8
        stack.setCustomSpacing(12, after: label)
        stack.edgeInsets = NSEdgeInsets(top: 8, left: 14, bottom: 8, right: actions.isEmpty ? 16 : 8)

        let content = HoverView()
        content.material = .hudWindow
        content.state = .active
        content.wantsLayer = true
        content.layer?.cornerRadius = 14
        content.layer?.cornerCurve = .continuous
        content.addSubview(stack)
        stack.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            stack.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            stack.topAnchor.constraint(equalTo: content.topAnchor),
            stack.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            label.widthAnchor.constraint(lessThanOrEqualToConstant: 360),
        ])

        let size = stack.fittingSize
        let screen = NSScreen.screens.first { $0.frame.contains(NSEvent.mouseLocation) } ?? NSScreen.main
        let visible = screen?.visibleFrame ?? .zero
        let frame = NSRect(x: visible.midX - size.width / 2, y: visible.maxY - size.height - 12,
                           width: size.width, height: size.height)

        let p = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel],
                        backing: .buffered, defer: false)
        p.isOpaque = false
        p.backgroundColor = .clear
        p.hasShadow = true
        p.level = .statusBar
        p.ignoresMouseEvents = actions.isEmpty
        p.becomesKeyOnlyIfNeeded = true
        p.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        p.contentView = content
        p.orderFrontRegardless()
        panel = p

        if actions.isEmpty {
            scheduleHide(after: 1.4)
        } else {
            // Quick actions stay 3 s, or until you paste or copy something else, whichever
            // comes first. Hovering keeps them open.
            deadline = Date().addingTimeInterval(actionsLifetime)
            scheduleHide(after: actionsLifetime)
            watchForPaste()
            content.onHover = { inside in
                if inside { hideWork?.cancel() } else { scheduleHide(after: max(1, deadline.timeIntervalSinceNow)) }
            }
        }
    }

    static func dismiss() { scheduleHide(after: 0) }

    /// Copying something else closes the bubble (its buttons would act on the old capture).
    private static func watchForPaste() {
        let startCount = NSPasteboard.general.changeCount
        clipboardTimer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { _ in
            if NSPasteboard.general.changeCount != startCount { scheduleHide(after: 0) }
        }
    }

    /// Called when the captured text is pasted somewhere: close the bubble.
    static func pasted() {
        guard panel?.isVisible == true, !targets.isEmpty else { return }
        scheduleHide(after: 0)
    }

    private static func stopWatchingForPaste() {
        clipboardTimer?.invalidate()
        clipboardTimer = nil
    }

    private static func scheduleHide(after delay: TimeInterval) {
        hideWork?.cancel()
        guard let p = panel else { return }
        let work = DispatchWorkItem {
            stopWatchingForPaste()
            NSAnimationContext.runAnimationGroup({ ctx in
                ctx.duration = 0.25
                p.animator().alphaValue = 0
            }, completionHandler: { p.orderOut(nil) })
        }
        hideWork = work
        DispatchQueue.main.asyncAfter(deadline: .now() + delay, execute: work)
    }

    private static func pillButton(_ title: String, target: ActionTarget) -> NSButton {
        let b = NSButton(title: title, target: target, action: #selector(ActionTarget.run))
        b.isBordered = false
        b.font = .systemFont(ofSize: 12, weight: .medium)
        b.contentTintColor = .labelColor
        b.wantsLayer = true
        b.layer?.cornerRadius = 9
        b.layer?.cornerCurve = .continuous
        b.layer?.backgroundColor = NSColor.labelColor.withAlphaComponent(0.1).cgColor
        b.translatesAutoresizingMaskIntoConstraints = false
        let width = (title as NSString).size(withAttributes: [.font: b.font!]).width + 20
        b.widthAnchor.constraint(equalToConstant: ceil(width)).isActive = true
        b.heightAnchor.constraint(equalToConstant: 22).isActive = true
        b.setAccessibilityLabel(title)
        return b
    }

    private final class ActionTarget: NSObject {
        let action: ToastAction
        init(_ action: ToastAction) { self.action = action }
        @objc func run() {
            if let confirmation = action.run() {
                Toast.show(confirmation, symbol: "checkmark")
            } else {
                Toast.dismiss()
            }
        }
    }

    private final class HoverView: NSVisualEffectView {
        var onHover: ((Bool) -> Void)?
        override func updateTrackingAreas() {
            super.updateTrackingAreas()
            trackingAreas.forEach(removeTrackingArea)
            addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways],
                                           owner: self, userInfo: nil))
        }
        override func mouseEntered(with event: NSEvent) { onHover?(true) }
        override func mouseExited(with event: NSEvent) { onHover?(false) }
    }
}
