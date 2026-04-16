// BotNexus WebUI — Audio recording via MediaRecorder API

import { debugLog, serverLog } from './api.js';

let mediaRecorder = null;
let audioChunks = [];
let recordingStartTime = null;

/**
 * Check if browser supports audio recording.
 */
export function isAudioRecordingSupported() {
    return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.MediaRecorder);
}

/**
 * Start recording audio from the microphone.
 * @returns {Promise<void>}
 */
export async function startRecording() {
    if (mediaRecorder && mediaRecorder.state === 'recording') {
        debugLog('audio', 'Already recording');
        return;
    }

    try {
        const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
        audioChunks = [];
        recordingStartTime = Date.now();

        // Prefer webm/opus, fall back to whatever the browser supports
        const mimeType = MediaRecorder.isTypeSupported('audio/webm;codecs=opus')
            ? 'audio/webm;codecs=opus'
            : MediaRecorder.isTypeSupported('audio/webm')
                ? 'audio/webm'
                : '';

        mediaRecorder = new MediaRecorder(stream, mimeType ? { mimeType } : {});

        mediaRecorder.ondataavailable = (e) => {
            if (e.data.size > 0) audioChunks.push(e.data);
        };

        mediaRecorder.onerror = (e) => {
            debugLog('audio', 'Recording error', e.error);
            serverLog('error', 'Audio recording error', { error: e.error?.message });
        };

        mediaRecorder.start(250); // Collect data every 250ms
        debugLog('audio', `Recording started (${mediaRecorder.mimeType})`);
        serverLog('info', 'Audio recording started', { mimeType: mediaRecorder.mimeType });
    } catch (err) {
        debugLog('audio', 'Failed to start recording', err);
        serverLog('error', 'Failed to start audio recording', { error: err.message });
        throw err;
    }
}

/**
 * Stop recording and return the audio as a base64-encoded string.
 * @returns {Promise<{base64: string, mimeType: string, durationMs: number, sizeBytes: number}>}
 */
export function stopRecording() {
    return new Promise((resolve, reject) => {
        if (!mediaRecorder || mediaRecorder.state === 'inactive') {
            reject(new Error('Not recording'));
            return;
        }

        mediaRecorder.onstop = async () => {
            const durationMs = Date.now() - recordingStartTime;
            const mimeType = mediaRecorder.mimeType || 'audio/webm';
            const blob = new Blob(audioChunks, { type: mimeType });
            audioChunks = [];

            // Stop all tracks to release the microphone
            mediaRecorder.stream.getTracks().forEach(t => t.stop());

            try {
                const arrayBuffer = await blob.arrayBuffer();
                const base64 = btoa(
                    new Uint8Array(arrayBuffer).reduce((data, byte) => data + String.fromCharCode(byte), '')
                );
                debugLog('audio', `Recording stopped: ${durationMs}ms, ${blob.size} bytes`);
                serverLog('info', 'Audio recording stopped', { durationMs, sizeBytes: blob.size, mimeType });
                resolve({ base64, mimeType, durationMs, sizeBytes: blob.size });
            } catch (err) {
                reject(err);
            }
        };

        mediaRecorder.stop();
    });
}

/**
 * Check if currently recording.
 */
export function isRecording() {
    return mediaRecorder !== null && mediaRecorder.state === 'recording';
}

/**
 * Cancel an active recording without returning data.
 */
export function cancelRecording() {
    if (mediaRecorder && mediaRecorder.state === 'recording') {
        mediaRecorder.onstop = () => {
            mediaRecorder.stream.getTracks().forEach(t => t.stop());
            audioChunks = [];
        };
        mediaRecorder.stop();
        debugLog('audio', 'Recording cancelled');
    }
}
