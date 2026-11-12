import Carbon

/// A global keyboard shortcut. Carbon hot keys need no Accessibility permission.
final class HotKey {
    private var ref: EventHotKeyRef?
    private static var handlerRef: EventHandlerRef?
    private static var actions: [UInt32: () -> Void] = [:]
    private static var nextID: UInt32 = 1
    private let id: UInt32

    init?(keyCode: UInt32, modifiers: UInt32, action: @escaping () -> Void) {
        id = HotKey.nextID
        HotKey.nextID += 1

        if HotKey.handlerRef == nil {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            let status = InstallEventHandler(GetApplicationEventTarget(), { _, event, _ in
                var hk = EventHotKeyID()
                GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                                  nil, MemoryLayout<EventHotKeyID>.size, nil, &hk)
                DispatchQueue.main.async { HotKey.actions[hk.id]?() }
                return noErr
            }, 1, &spec, nil, &HotKey.handlerRef)
            guard status == noErr else { return nil }
        }

        let hkID = EventHotKeyID(signature: OSType(0x534E_4F57), id: id) // 'SNOW'
        guard RegisterEventHotKey(keyCode, modifiers, hkID, GetApplicationEventTarget(), 0, &ref) == noErr else { return nil }
        HotKey.actions[id] = action
    }

    deinit {
        if let ref { UnregisterEventHotKey(ref) }
        HotKey.actions[id] = nil
    }
}
