import Foundation

/// One saved credential. The JSON names here must match the C# ones exactly - they are
/// written by System.Text.Json with a camelCase policy, which lines up with these property
/// names, so no CodingKeys are needed.
///
/// A deleted entry is kept with `deleted = true` rather than removed, so that once sync
/// exists a deletion cannot be undone by the other device.
public struct Entry: Codable, Identifiable, Equatable, Sendable {
    public var id: String
    public var name: String
    public var username: String?
    public var email: String?
    public var password: String?
    public var notes: String?

    /// Unix time in milliseconds.
    public var updatedAt: Int64
    public var deleted: Bool

    public init(
        id: String, name: String,
        username: String? = nil, email: String? = nil,
        password: String? = nil, notes: String? = nil,
        updatedAt: Int64 = 0, deleted: Bool = false
    ) {
        self.id = id
        self.name = name
        self.username = username
        self.email = email
        self.password = password
        self.notes = notes
        self.updatedAt = updatedAt
        self.deleted = deleted
    }

    /// Who this login is for, for the list on screen.
    public var who: String { username ?? email ?? "" }

    public func matches(_ search: String) -> Bool {
        let needle = search.trimmingCharacters(in: .whitespaces)
        guard !needle.isEmpty else { return true }

        return [name, username, email, notes]
            .compactMap { $0 }
            .contains { $0.range(of: needle, options: .caseInsensitive) != nil }
    }
}
