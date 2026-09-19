import UIKit
import UniformTypeIdentifiers

final class ShareViewController: UIViewController {
    private let statusLabel = UILabel()
    private let doneButton = UIButton(type: .system)
    private var processed = false

    override func viewDidLoad() {
        super.viewDidLoad()

        view.backgroundColor = .systemBackground
        preferredContentSize = CGSize(width: 360, height: 180)

        statusLabel.numberOfLines = 0
        statusLabel.textAlignment = .center
        statusLabel.text = "공유 내용을 확인하고 있습니다…"

        doneButton.setTitle("완료", for: .normal)
        doneButton.isEnabled = false
        doneButton.addTarget(self, action: #selector(complete), for: .touchUpInside)

        let stack = UIStackView(arrangedSubviews: [statusLabel, doneButton])
        stack.axis = .vertical
        stack.spacing = 14
        stack.translatesAutoresizingMaskIntoConstraints = false
        view.addSubview(stack)

        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -24),
            stack.centerYAnchor.constraint(equalTo: view.centerYAnchor)
        ])
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        guard !processed else { return }
        processed = true
        processShare()
    }

    private func processShare() {
        let items = extensionContext?.inputItems as? [NSExtensionItem] ?? []
        let providers = items.flatMap { $0.attachments ?? [] }

        if let image = providers.first(where: {
            $0.hasItemConformingToTypeIdentifier(UTType.image.identifier)
        }) {
            stageImage(image)
            return
        }

        if let text = providers.first(where: {
            $0.hasItemConformingToTypeIdentifier(UTType.plainText.identifier)
        }) {
            stageText(text)
            return
        }

        if let url = providers.first(where: {
            $0.hasItemConformingToTypeIdentifier(UTType.url.identifier)
        }) {
            stageURL(url)
            return
        }

        finishStaging(.failure(ShareError.unsupported))
    }

    private func stageText(_ provider: NSItemProvider) {
        provider.loadItem(
            forTypeIdentifier: UTType.plainText.identifier,
            options: nil
        ) { item, error in
            if let error {
                self.finishStaging(.failure(error))
                return
            }

            let text: String?
            if let value = item as? String {
                text = value
            } else if let value = item as? NSAttributedString {
                text = value.string
            } else {
                text = nil
            }

            guard let text, !text.isEmpty else {
                self.finishStaging(.failure(ShareError.empty))
                return
            }

            do {
                try SharedPayloadStore.saveText(text)
                self.finishStaging(.success(()))
            } catch {
                self.finishStaging(.failure(error))
            }
        }
    }

    private func stageURL(_ provider: NSItemProvider) {
        provider.loadItem(
            forTypeIdentifier: UTType.url.identifier,
            options: nil
        ) { item, error in
            if let error {
                self.finishStaging(.failure(error))
                return
            }

            let text: String?
            if let url = item as? URL {
                text = url.absoluteString
            } else if let url = item as? NSURL {
                text = url.absoluteString
            } else if let value = item as? String {
                text = value
            } else {
                text = nil
            }

            guard let text, !text.isEmpty else {
                self.finishStaging(.failure(ShareError.empty))
                return
            }

            do {
                try SharedPayloadStore.saveText(text)
                self.finishStaging(.success(()))
            } catch {
                self.finishStaging(.failure(error))
            }
        }
    }

    private func stageImage(_ provider: NSItemProvider) {
        provider.loadFileRepresentation(
            forTypeIdentifier: UTType.image.identifier
        ) { url, fileError in
            if let url {
                do {
                    try SharedPayloadStore.saveImageFile(url)
                    self.finishStaging(.success(()))
                    return
                } catch SharedPayloadStore.StoreError.tooLarge {
                    self.finishStaging(.failure(
                        SharedPayloadStore.StoreError.tooLarge))
                    return
                } catch {
                    // Some providers do not expose a reliable file-size/resource
                    // representation. Fall back to a bounded in-memory representation.
                }
            }

            provider.loadDataRepresentation(
                forTypeIdentifier: UTType.image.identifier
            ) { data, dataError in
                do {
                    guard let data else {
                        throw dataError ?? fileError ?? ShareError.empty
                    }
                    try SharedPayloadStore.saveImageData(data)
                    self.finishStaging(.success(()))
                } catch {
                    self.finishStaging(.failure(error))
                }
            }
        }
    }

    private func finishStaging(_ result: Result<Void, Error>) {
        DispatchQueue.main.async {
            self.doneButton.isEnabled = true

            switch result {
            case .success:
                self.statusLabel.text =
                    "PC 전송 대기열에 추가했습니다. Con.Phone 앱을 열면 자동으로 전송합니다."
            case .failure(let error):
                self.statusLabel.text =
                    "공유 준비 실패: \(error.localizedDescription)"
            }
        }
    }

    @objc private func complete() {
        extensionContext?.completeRequest(returningItems: nil)
    }

    private enum ShareError: LocalizedError {
        case unsupported
        case empty

        var errorDescription: String? {
            switch self {
            case .unsupported:
                return "텍스트 또는 이미지만 지원합니다."
            case .empty:
                return "공유할 내용이 비어 있습니다."
            }
        }
    }
}
