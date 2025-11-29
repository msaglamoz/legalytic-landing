// Copoint verify shell → main.js
// Root config'i okuyalım
const rootEl = document.getElementById("root");
let CopointConfig = {};
try {
    CopointConfig = JSON.parse(rootEl.getAttribute("data-config") || "{}");
    console.log("CopointConfig:", CopointConfig);
} catch (e) {
    console.warn("data-config parse edilemedi:", e);
}

// DOM referansları
const video = document.getElementById("video");
const statusEl = document.getElementById("status");
const stepIndicator = document.getElementById("step-indicator");
const hintEl = document.getElementById("hint");
const frameGuide = document.getElementById("frame-guide");
const selfieCircle = document.getElementById("selfie-circle");

const processCanvas = document.getElementById("process-canvas");
// Sık sık getImageData okuduğumuz canvas → willReadFrequently:true
const processCtx = processCanvas.getContext("2d", { willReadFrequently: true });
const captureCanvas = document.getElementById("capture-canvas");
const captureCtx = captureCanvas.getContext("2d");

const debugRect = document.getElementById("debug-rect");

const debugToggle = document.getElementById("debug-toggle");
const fakeCameraToggle = document.getElementById("fake-camera-toggle");
const blurInput = document.getElementById("blur-threshold");
const stableInput = document.getElementById("stable-frames");
const minAreaInput = document.getElementById("min-area");
const debugInfo = document.getElementById("debug-info");
const testImageInput = document.getElementById("test-image-input");

const restartBtn = document.getElementById("restart-btn");
const manualBtn = document.getElementById("manual-btn");
const nextStepBtn = document.getElementById("next-step-btn");
const submitBtn = document.getElementById("submit-btn");

const previewFront = document.getElementById("preview-front");
const previewBack = document.getElementById("preview-back");
const previewSelfie = document.getElementById("preview-selfie");

// Durum değişkenleri
let stream = null;
let cameraInitialized = false;
let processing = false;
let captured = false;
let cvReady = false;

const steps = ["id_front", "id_back", "selfie"];
let currentStepIndex = 0;
let frontImage = null;
let backImage = null;
let selfieImage = null;

let stableFrames = 0;
let REQUIRED_STABLE_FRAMES = 8;
let BLUR_THRESHOLD = 80.0;
let MIN_CARD_AREA_RATIO = 0.15;

let DEBUG_MODE = false;
let USE_FAKE_CAMERA = false;
let testImage = null;

let lastRect = null;
let currentFacingMode = "environment"; // aktif kamera yönü

function updateUIForStep() {
    const step = steps[currentStepIndex];
    const titles = {
        "id_front": "Adım 1 / 3 – Kimlik ön yüz",
        "id_back": "Adım 2 / 3 – Kimlik arka yüz",
        "selfie": "Adım 3 / 3 – Selfie"
    };

    const hints = {
        "id_front": "Kimliğin ön yüzünü çerçevenin içine yerleştir, sabit tut.",
        "id_back": "Kimliğin arka yüzünü çerçevenin içine yerleştir, sabit tut.",
        "selfie": "Yüzünü dairenin içine al, iyi aydınlatılmış bir ortamda dur."
    };

    stepIndicator.textContent = titles[step];
    hintEl.textContent = hints[step];

    const isSelfie = step === "selfie";
    frameGuide.style.display = isSelfie ? "none" : "block";
    selfieCircle.style.display = isSelfie ? "block" : "none";

    manualBtn.disabled = !cvReady;
    restartBtn.disabled = true;
    nextStepBtn.disabled = !captured;

    if (isSelfie) {
        statusEl.textContent = "Selfie adımı – yüzünü kadraja al.";
    } else {
        statusEl.textContent = "Kamerayı kimliğe doğrultun…";
    }
}

function setupCanvasSize() {
    const width = video.videoWidth || 720;
    const height = video.videoHeight || 960;
    processCanvas.width = width;
    processCanvas.height = height;
    captureCanvas.width = width;
    captureCanvas.height = height;
}

// KAMERA SADECE BİR KEZ AÇILIYOR (arka kamera ile)
async function initCamera() {
    try {
        if (cameraInitialized) return; // ikinci kez asla açma

        const constraints = {
            video: {
                facingMode: "environment"
            },
            audio: false
        };

        stream = await navigator.mediaDevices.getUserMedia(constraints);
        cameraInitialized = true;
        currentFacingMode = "environment";

        video.srcObject = stream;

        video.onloadedmetadata = () => {
            setupCanvasSize();
            captured = false;
            updateUIForStep();
            startProcessingLoop();
        };
    } catch (err) {
        console.error(err);
        statusEl.textContent = "Kamera erişimi başarısız oldu.";
    }
}

// Mevcut stream üzerinden ön/arka kamera değiştirme denemesi
async function ensureCameraFacing(mode) {
    if (!stream) return;
    if (currentFacingMode === mode) return;

    const tracks = stream.getVideoTracks();
    if (!tracks || !tracks[0]) return;
    const track = tracks[0];

    if (track.applyConstraints) {
        try {
            await track.applyConstraints({ facingMode: mode });
            currentFacingMode = mode;
            console.log("Kamera yönü değişti:", mode);
        } catch (e) {
            console.warn("facingMode değiştirilemedi, mevcut kamera kullanılacak:", e);
        }
    }
}

function startProcessingLoop() {
    if (processing || !cvReady) return;
    processing = true;
    captured = false;
    stableFrames = 0;
    lastRect = null;
    hideDebugRect();

    const FPS = 12;
    const interval = 1000 / FPS;

    const loop = () => {
        if (!processing) return;
        processFrame();
        setTimeout(loop, interval);
    };
    loop();
}

function processFrame() {
    if (captured) return;

    const step = steps[currentStepIndex];

    // Selfie adımında auto-capture yok; sadece manuel çekim
    if (step === "selfie") {
        return;
    }

    if (USE_FAKE_CAMERA) {
        if (!testImage || !testImage.width || !testImage.height) return;
        processCtx.drawImage(testImage, 0, 0, processCanvas.width, processCanvas.height);
    } else {
        if (!video.videoWidth || !video.videoHeight) return;
        processCtx.drawImage(video, 0, 0, processCanvas.width, processCanvas.height);
    }

    const frame = processCtx.getImageData(0, 0, processCanvas.width, processCanvas.height);

    let src = cv.matFromImageData(frame);
    let gray = new cv.Mat();
    let blurred = new cv.Mat();
    let edges = new cv.Mat();

    cv.cvtColor(src, gray, cv.COLOR_RGBA2GRAY, 0);
    cv.GaussianBlur(gray, blurred, new cv.Size(5, 5), 0, 0, cv.BORDER_DEFAULT);
    cv.Canny(blurred, edges, 50, 150);

    let lap = new cv.Mat();
    cv.Laplacian(gray, lap, cv.CV_64F);
    const blurScore = lapVariance(lap);

    let contours = new cv.MatVector();
    let hierarchy = new cv.Mat();
    cv.findContours(edges, contours, hierarchy, cv.RETR_EXTERNAL, cv.CHAIN_APPROX_SIMPLE);

    const bestRect = findBestCardRect(contours, src.cols, src.rows);

    src.delete();
    gray.delete();
    blurred.delete();
    edges.delete();
    lap.delete();
    contours.delete();
    hierarchy.delete();

    if (bestRect) {
        const { x, y, width: w, height: h } = bestRect;
        drawDebugRect(x, y, w, h);
        const areaRatio = (w * h) / (processCanvas.width * processCanvas.height);

        const stable = isStable(bestRect);
        const blurOk = blurScore >= BLUR_THRESHOLD;
        const areaOk = areaRatio >= MIN_CARD_AREA_RATIO;

        if (DEBUG_MODE && debugInfo) {
            const lines = [
                `Adım: ${step}`,
                `Blur skoru: ${blurScore.toFixed(2)} (eşik: ${BLUR_THRESHOLD})`,
                `Alan oranı: ${(areaRatio * 100).toFixed(1)}% (min: ${(MIN_CARD_AREA_RATIO * 100).toFixed(1)}%)`,
                `Stabil frame: ${stableFrames}/${REQUIRED_STABLE_FRAMES}`,
                `Stable? ${stable ? "Evet" : "Hayır"}`,
                `Blur OK? ${blurOk ? "Evet" : "Hayır"}`,
                `Alan OK? ${areaOk ? "Evet" : "Hayır"}`,
                `Kaynak: ${USE_FAKE_CAMERA ? "Fake görüntü" : "Kamera"}`
            ];
            debugInfo.textContent = lines.join("\n");
        }

        if (stable && blurOk && areaOk) {
            stableFrames++;
            statusEl.textContent = `Kimlik algılandı, sabit bekleniyor… (${stableFrames}/${REQUIRED_STABLE_FRAMES})`;
        } else {
            stableFrames = 0;
            statusEl.textContent = `Kimlik arıyor… (Netlik: ${blurScore.toFixed(0)})`;
        }

        if (stableFrames >= REQUIRED_STABLE_FRAMES) {
            autoCapture(bestRect);
        }
    } else {
        hideDebugRect();
        stableFrames = 0;
        statusEl.textContent = `Kimlik arıyor…`;
    }
}

function lapVariance(mat) {
    let data = mat.data64F;
    if (!data || data.length === 0) return 0.0;

    let sum = 0;
    for (let i = 0; i < data.length; i++) sum += data[i];
    const mean = sum / data.length;

    let sqDiff = 0;
    for (let i = 0; i < data.length; i++) {
        const diff = data[i] - mean;
        sqDiff += diff * diff;
    }
    return sqDiff / data.length;
}

function findBestCardRect(contours, width, height) {
    let best = null;
    let bestArea = 0;

    for (let i = 0; i < contours.size(); i++) {
        const cnt = contours.get(i);
        const area = cv.contourArea(cnt);
        if (area < (width * height * 0.05)) continue;

        const peri = cv.arcLength(cnt, true);
        const approx = new cv.Mat();
        cv.approxPolyDP(cnt, approx, 0.02 * peri, true);

        if (approx.rows === 4) {
            const rect = cv.boundingRect(approx);
            const { width: w, height: h } = rect;
            const ratio = w / h;

            if (ratio < 1.3 || ratio > 1.9) {
                approx.delete();
                continue;
            }

            if (area > bestArea) {
                best = rect;
                bestArea = area;
            }
        }
    }

    return best;
}

function drawDebugRect(x, y, w, h) {
    debugRect.style.display = "block";
    debugRect.style.left = `${(x / processCanvas.width) * 100}%`;
    debugRect.style.top = `${(y / processCanvas.height) * 100}%`;
    debugRect.style.width = `${(w / processCanvas.width) * 100}%`;
    debugRect.style.height = `${(h / processCanvas.height) * 100}%`;
}

function hideDebugRect() {
    debugRect.style.display = "none";
}

function isStable(rect) {
    if (!lastRect) {
        lastRect = rect;
        return false;
    }

    const dx = Math.abs(rect.x - lastRect.x);
    const dy = Math.abs(rect.y - lastRect.y);
    const dw = Math.abs(rect.width - lastRect.width);
    const dh = Math.abs(rect.height - lastRect.height);

    const tolPos = 6;
    const tolSize = 8;

    const stable = dx < tolPos && dy < tolPos && dw < tolSize && dh < tolSize;
    lastRect = rect;
    return stable;
}

function autoCapture(rect) {
    captured = true;
    processing = false;
    stableFrames = 0;

    const { x, y, width: w, height: h } = rect;

    captureCtx.drawImage(processCanvas, 0, 0);
    const crop = captureCtx.getImageData(x, y, w, h);
    captureCanvas.width = w;
    captureCanvas.height = h;
    captureCtx.putImageData(crop, 0, 0);

    const dataUrl = captureCanvas.toDataURL("image/jpeg", 0.92);
    const step = steps[currentStepIndex];

    if (step === "id_front") {
        frontImage = dataUrl;
        previewFront.src = dataUrl;
    } else if (step === "id_back") {
        backImage = dataUrl;
        previewBack.src = dataUrl;
    }

    statusEl.textContent = "Fotoğraf yakalandı. Gerekirse yeniden çekebilirsiniz.";
    restartBtn.disabled = false;
    nextStepBtn.disabled = false;

    if (frontImage && backImage && selfieImage) {
        submitBtn.disabled = false;
    }
}

function manualCapture() {
    const step = steps[currentStepIndex];

    setupCanvasSize();
    captureCtx.drawImage(video, 0, 0, captureCanvas.width, captureCanvas.height);
    const dataUrl = captureCanvas.toDataURL("image/jpeg", 0.92);

    if (step === "selfie") {
        selfieImage = dataUrl;
        previewSelfie.src = dataUrl;
        statusEl.textContent = "Selfie çekildi.";
    } else if (step === "id_front") {
        frontImage = dataUrl;
        previewFront.src = dataUrl;
        statusEl.textContent = "Manuel ön yüz çekildi.";
    } else if (step === "id_back") {
        backImage = dataUrl;
        previewBack.src = dataUrl;
        statusEl.textContent = "Manuel arka yüz çekildi.";
    }

    captured = true;
    restartBtn.disabled = false;
    nextStepBtn.disabled = false;

    if (frontImage && backImage && selfieImage) {
        submitBtn.disabled = false;
    }
}

function resetCurrentStep() {
    const step = steps[currentStepIndex];

    if (step === "id_front") {
        frontImage = null;
        previewFront.src = "";
    } else if (step === "id_back") {
        backImage = null;
        previewBack.src = "";
    } else if (step === "selfie") {
        selfieImage = null;
        previewSelfie.src = "";
    }

    captured = false;
    restartBtn.disabled = true;
    nextStepBtn.disabled = true;
    stableFrames = 0;
    statusEl.textContent = "Bu adımı yeniden çekebilirsiniz.";
    processing = false;
    hideDebugRect();
    setupCanvasSize();
    startProcessingLoop();
    submitBtn.disabled = !(frontImage && backImage && selfieImage);
}

// 🔹 Artık async: Selfie adımında ön kameraya geçmeyi deniyoruz
async function goToNextStep() {
    if (currentStepIndex < steps.length - 1) {
        currentStepIndex++;
        processing = false;
        captured = false;
        stableFrames = 0;
        hideDebugRect();
        updateUIForStep();

        const step = steps[currentStepIndex];

        if (step === "selfie") {
            // Selfie adımında ön kamera
            await ensureCameraFacing("user");
        } else {
            // Diğer adımlarda arka kamera
            await ensureCameraFacing("environment");
        }

        setupCanvasSize();
        startProcessingLoop();
    }
}

function submitAll() {
    if (!frontImage || !backImage || !selfieImage) return;

    const payload = {
        id_front: frontImage,
        id_back: backImage,
        selfie: selfieImage,
        meta: {
            env: CopointConfig.ENV || "dev",
            apiHost: CopointConfig.API_HOST || null
        }
    };

    console.log("SUBMIT PAYLOAD (TEST MOD):", payload);
    statusEl.textContent = "Test mod: payload console.log içinde. Backend'e gönderilmiyor.";
    alert("Test mod: Payload console.log içinde. Backend entegrasyonu yok.");
}

// OpenCV hazır olduğunda
function onOpenCvReady() {
    cvReady = true;
    statusEl.textContent = "OpenCV hazır. Kamera açılıyor…";
    updateUIForStep();

    if (!USE_FAKE_CAMERA) {
        initCamera(); // SADECE BİR KEZ
    } else {
        setupCanvasSize();
        captured = false;
        startProcessingLoop();
    }
}

// OpenCV yüklemesi için callback ayarı
if (typeof cv !== "undefined") {
    cv["onRuntimeInitialized"] = onOpenCvReady;
} else {
    window.Module = {
        onRuntimeInitialized: onOpenCvReady
    };
}

// Event binding
restartBtn.addEventListener("click", resetCurrentStep);
manualBtn.addEventListener("click", manualCapture);
nextStepBtn.addEventListener("click", () => { goToNextStep(); });
submitBtn.addEventListener("click", submitAll);

// Test panel
if (debugToggle) {
    debugToggle.addEventListener("change", (e) => {
        DEBUG_MODE = e.target.checked;
        if (!DEBUG_MODE && debugInfo) {
            debugInfo.textContent = "";
        }
    });
}

if (blurInput) {
    blurInput.addEventListener("change", (e) => {
        const v = parseFloat(e.target.value);
        if (!isNaN(v)) BLUR_THRESHOLD = v;
    });
}

if (stableInput) {
    stableInput.addEventListener("change", (e) => {
        const v = parseInt(e.target.value, 10);
        if (!isNaN(v) && v > 0) REQUIRED_STABLE_FRAMES = v;
    });
}

if (minAreaInput) {
    minAreaInput.addEventListener("change", (e) => {
        const v = parseFloat(e.target.value);
        if (!isNaN(v) && v > 0 && v < 1) MIN_CARD_AREA_RATIO = v;
    });
}

if (fakeCameraToggle) {
    fakeCameraToggle.addEventListener("change", (e) => {
        USE_FAKE_CAMERA = e.target.checked;
        if (USE_FAKE_CAMERA) {
            if (testImageInput) testImageInput.disabled = false;
            // Kamerayı ARTIK durdurmuyoruz → yeniden izin isteme ihtimali yok
            setupCanvasSize();
            captured = false;
            startProcessingLoop();
        } else {
            if (testImageInput) testImageInput.disabled = true;
            testImage = null;
            captured = false;
            processing = false;
            hideDebugRect();
            setupCanvasSize();
            startProcessingLoop();
        }
    });
}

if (testImageInput) {
    testImageInput.addEventListener("change", (e) => {
        const file = e.target.files && e.target.files[0];
        if (!file) return;
        const img = new Image();
        img.onload = () => {
            testImage = img;
            setupCanvasSize();
            captured = false;
            if (cvReady && USE_FAKE_CAMERA) {
                startProcessingLoop();
            }
        };
        img.src = URL.createObjectURL(file);
    });
}
