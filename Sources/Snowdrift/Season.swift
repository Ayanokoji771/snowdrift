import Foundation

/// The four seasons. Snow in winter, cherry petals in spring, dandelion fluff and fireflies in summer,
/// falling leaves in autumn.
enum Season: Int, CaseIterable {
    case winter = 0, spring, summer, autumn

    /// Countries south of the equator: the seasons there are the other way round.
    private static let southern: Set<String> = [
        "AU", "NZ", "AR", "CL", "UY", "PY", "ZA", "NA", "BW", "LS", "SZ", "MZ", "ZW", "ZM", "MW", "MG", "BR", "BO", "PE",
    ]

    /// The season right now for this Mac's region (meteorological: winter is December to February).
    static func now(_ date: Date = Date()) -> Season {
        let month = Calendar.current.component(.month, from: date)
        var index = (month % 12) / 3   // Dec–Feb 0, Mar–May 1, Jun–Aug 2, Sep–Nov 3
        if let region = Locale.current.region?.identifier, southern.contains(region) { index = (index + 2) % 4 }
        return Season(rawValue: index) ?? .winter
    }

    /// The setting: -1 follows the calendar.
    static func resolve(_ setting: Int) -> Season {
        setting >= 0 ? Season(rawValue: setting) ?? .winter : now()
    }

    /// Evening and night, when fireflies come out.
    static func isEvening(_ date: Date = Date()) -> Bool {
        let h = Calendar.current.component(.hour, from: date)
        return h >= 19 || h < 6
    }

    var name: String {
        switch self {
        case .winter: return L("Зима ❄️", "Winter ❄️")
        case .spring: return L("Весна 🌸", "Spring 🌸")
        case .summer: return L("Лето ☀️", "Summer ☀️")
        case .autumn: return L("Осень 🍂", "Autumn 🍂")
        }
    }

    /// The setting that the island and other views read (kept current by the app).
    static var current: Season = .winter
}
