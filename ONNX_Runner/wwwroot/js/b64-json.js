// Handles the Base64 JSON response representation used by Tsubaki.
// The embedded audio is MP3; JSON/Base64 is only the transport representation.
export async function receiveBase64Json(
    response,
    {
        stream = false,
        onChunk = null
    } = {}
) {
    const jsonBlob =
        stream
            ? await readStreamedJson(
                response.body,
                onChunk
            )
            : await response.blob();

    const payload =
        await parseJsonBlob(
            jsonBlob
        );

    const audioContent =
        payload?.audioContent;

    if (
        typeof audioContent !== 'string' ||
        audioContent.length === 0
    ) {
        throw new Error(
            'Base64 JSON response does not contain a valid audioContent field.'
        );
    }

    return {
        jsonBlob,
        audioBlob:
            base64ToBlob(
                audioContent,
                'audio/mpeg'
            )
    };
}

async function readStreamedJson(
    body,
    onChunk
) {
    if (!body) {
        throw new Error(
            'Response body is not available for Base64 JSON streaming.'
        );
    }

    const reader =
        body.getReader();

    const chunks = [];

    while (true) {
        const {
            done,
            value
        } = await reader.read();

        if (done) {
            break;
        }

        chunks.push(value);

        if (onChunk) {
            onChunk(value.length);
        }
    }

    return new Blob(
        chunks,
        {
            type: 'application/json'
        }
    );
}

async function parseJsonBlob(jsonBlob) {
    const text =
        await jsonBlob.text();

    try {
        return JSON.parse(text);
    }
    catch {
        throw new Error(
            'The Base64 JSON response is incomplete or invalid.'
        );
    }
}

function base64ToBlob(
    base64,
    mimeType
) {
    let binary;

    try {
        binary = atob(base64);
    }
    catch {
        throw new Error(
            'audioContent is not valid Base64 data.'
        );
    }

    const bytes =
        new Uint8Array(
            binary.length
        );

    for (
        let i = 0;
        i < binary.length;
        i++
    ) {
        bytes[i] =
            binary.charCodeAt(i);
    }

    return new Blob(
        [bytes],
        {
            type: mimeType
        }
    );
}
