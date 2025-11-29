    const video = document.getElementById("video");
    const statusEl = document.getElementById("status");
    const stepIndicator = document.getElementById("step-indicator");
    const hintEl = document.getElementById("hint");
    const frameGuide = document.getElementById("frame-guide");
    const selfieCircle = document.getElementById("selfie-circle");

    const processCanvas = document.getElementById("process-canvas");
    const processCtx = processCanvas.getContext("2d");
    const captureCanvas = document.getElementById("capture-canvas");
    const captureCtx = captureCanvas.getContext("2d");

    const debugRect = document.getElementById("debug-rect");

    const restartBtn = document.getElementById("restart-btn");
    const manualBtn = document.getElementById("manual-btn");
    const nextStepBtn = document.getElementById("next-step-btn");
    const submitBtn = document.getElementById("submit-btn");

    const previewFront = document.getElementById("preview-front");
    const previewBack = document.getElementById("preview-back");
    const previewSelfie = document.getElementById("preview-selfie");

    let stream = null;
    let processing = false;
    let captured = false;
    let cvReady = false;

    // Cihaz listesi
    let videoDevices = [];

    // Wizard state
    const steps = ["id_front", "id_back", "selfie"];
    let currentStepIndex = 0;
    let frontImage = null;
    let backImage = null;
    let selfieImage = null;

    // Stabilite için basit state (ön/arka)
    let stableFrames = 0;
    const REQUIRED_STABLE_FRAMES = 8;
    const BLUR_THRESHOLD = 80.0;
    const MIN_CARD_AREA_RATIO = 0.15;

    let lastRect = null;

    function updateUIForStep() {
        const step = steps[currentStepIndex];

        const labels = {
            id_front: "Adım 1 / 3 – Kimlik ön yüz",
            id_back:  "Adım 2 / 3 – Kimlik arka yüz",
            selfie:   "Adım 3 / 3 – Selfie"
        };

        const hints = {
            id_front: "Kimliği çerçevenin içine yerleştir, sabit tut. Netleşince otomatik fotoğraf alınacak.",
            id_back:  "Bu kez kimliğin arka yüzünü çerçeveye yerleştir, sabit tut.",
            selfie:   "Yüzünü daire içine al, ışığı yüzüne çevir ve 'Manuel çek' butonuna dokun."
        };

        stepIndicator.textContent = labels[step];
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

    function onOpenCvReady() {
        cvReady = true;
        statusEl.textContent = "OpenCV hazır. Kamera açılıyor…";
        initCamera();
    }

    if (typeof cv !== "undefined") {
        cv["onRuntimeInitialized"] = onOpenCvReady;
    } else {
        window.Module = {
            onRuntimeInitialized: onOpenCvReady
        };
    }

    async function refreshVideoDevices() {
        try {
            const devices = await navigator.mediaDevices.enumerateDevices();
            videoDevices = devices.filter(d => d.kind === "videoinput");
            console.log("Video devices:", videoDevices);
        } catch (e) {
            console.warn("enumerateDevices başarısız:", e);
        }
    }

    // 💡 Kamerayı adım durumuna göre cihaz ID'si üzerinden seçiyoruz


async function initCamera() {
    try {
        const step = steps[currentStepIndex];
        const isSelfie = step === "selfie";

        // En az bir kere izin verildiyse label'lar dolar
        if (!videoDevices.length) {
            await refreshVideoDevices();
        }

        let videoConstraints = {};

        if (videoDevices.length) {
            if (isSelfie) {
                // 🔹 SELFIE: Ön kamera
                let frontCam = videoDevices.find(d =>
                    /front|user|ön/i.test(d.label)
                );

                // Label yoksa / bulunamadıysa:
                if (!frontCam) {
                    if (videoDevices.length === 2) {
                        // Çoğu telefonda 0 = ön, 1 = arka gibi davranıyorsun şu an
                        frontCam = videoDevices[0];
                    } else {
                        // 2'den fazlaysa, yine ilk cihazı ön varsay
                        frontCam = videoDevices[0];
                    }
                }

                videoConstraints = { deviceId: { exact: frontCam.deviceId } };
            } else {
                // 🔹 KİMLİK ADIMLARI: Arka kamera
                let backCam = videoDevices.find(d =>
                    /back|rear|environment|arka/i.test(d.label)
                );

                if (!backCam) {
                    if (videoDevices.length === 2) {
                        // 0'ı ön, 1'i arka kabul ediyoruz
                        backCam = videoDevices[1];
                    } else {
                        // Emin olamıyorsak son cihazı arka varsay
                        backCam = videoDevices[videoDevices.length - 1];
                    }
                }

                videoConstraints = { deviceId: { exact: backCam.deviceId } };
            }
        } else {
            // Cihaz listesi yoksa eski davranış (en son çare)
            videoConstraints = {
                facingMode: isSelfie ? "user" : "environment"
            };
        }

        const constraints = {
            video: videoConstraints,
            audio: false
        };

        // Eski stream varsa durdur
        if (stream) {
            stream.getTracks().forEach(t => t.stop());
        }

        console.log("Kamera constraints:", constraints);

        stream = await navigator.mediaDevices.getUserMedia(constraints);
        video.srcObject = stream;

        // İlk kez stream açıldıysa enumerateDevices yap, label'lar dolsun
        if (!videoDevices.length) {
            await refreshVideoDevices();
        }

        video.onloadedmetadata = () => {
            setupCanvasSize();
            captured = false;
            updateUIForStep();
            startProcessingLoop();
        };
    } catch (err) {
        console.error(err);
        statusEl.textContent = "Kamera açılamadı: " + err.message;
    }
}


    function setupCanvasSize() {
        const vw = video.videoWidth;
        const vh = video.videoHeight;

        const width = vw || 480;
        const height = vh || 640;

        processCanvas.width = width;
        processCanvas.height = height;

        captureCanvas.width = width;
        captureCanvas.height = height;
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
        if (!video.videoWidth || !video.videoHeight) return;
        if (captured) return;

        const step = steps[currentStepIndex];

        // Selfie adımında OpenCV ile auto-capture yok, manuel
        if (step === "selfie") {
            return;
        }

        processCtx.drawImage(video, 0, 0, processCanvas.width, processCanvas.height);
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
            const { x, y, w, h } = bestRect;
            drawDebugRect(x, y, w, h);
            const areaRatio = (w * h) / (processCanvas.width * processCanvas.height);

            const stable = isStable(bestRect);
            const blurOk = blurScore >= BLUR_THRESHOLD;
            const areaOk = areaRatio >= MIN_CARD_AREA_RATIO;

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
                const rArea = rect.width * rect.height;
                const ratio = rect.width / rect.height;
                if (ratio < 1.3 || ratio > 1.9) {
                    approx.delete();
                    continue;
                }

                if (rArea > bestArea) {
                    bestArea = rArea;
                    best = rect;
                }
            }
            approx.delete();
        }

        if (!best) return null;

        return {
            x: best.x,
            y: best.y,
            w: best.width,
            h: best.height
        };
    }

    function drawDebugRect(x, y, w, h) {
        const container = document.getElementById("video-container");
        const cw = container.clientWidth;
        const ch = container.clientHeight;

        const scaleX = cw / processCanvas.width;
        const scaleY = ch / processCanvas.height;

        debugRect.style.display = "block";
        debugRect.style.left = (x * scaleX) + "px";
        debugRect.style.top = (y * scaleY) + "px";
        debugRect.style.width = (w * scaleX) + "px";
        debugRect.style.height = (h * scaleY) + "px";
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
        const dw = Math.abs(rect.w - lastRect.w);
        const dh = Math.abs(rect.h - lastRect.h);

        lastRect = rect;
        return dx < 5 && dy < 5 && dw < 5 && dh < 5;
    }

    function autoCapture(rect) {
        if (captured) return;
        captured = true;
        processing = false;

        statusEl.textContent = "Otomatik fotoğraf alındı ✅";

        captureCtx.drawImage(video, 0, 0, captureCanvas.width, captureCanvas.height);
        const { x, y, w, h } = rect;
        const cropped = captureCtx.getImageData(x, y, w, h);

        const tmpCanvas = document.createElement("canvas");
        tmpCanvas.width = w;
        tmpCanvas.height = h;
        const tmpCtx = tmpCanvas.getContext("2d");
        tmpCtx.putImageData(cropped, 0, 0);

        const dataUrl = tmpCanvas.toDataURL("image/jpeg", 0.95);

        storeImageForCurrentStep(dataUrl);

        restartBtn.disabled = false;
        manualBtn.disabled = true;
        nextStepBtn.disabled = false;
    }

    function manualCapture() {
        if (captured) return;

        processing = false;
        captured = true;

        statusEl.textContent = "Manuel fotoğraf alındı ✅";

        captureCtx.drawImage(video, 0, 0, captureCanvas.width, captureCanvas.height);
        const dataUrl = captureCanvas.toDataURL("image/jpeg", 0.95);

        storeImageForCurrentStep(dataUrl);

        restartBtn.disabled = false;
        manualBtn.disabled = true;
        nextStepBtn.disabled = false;
    }

    function storeImageForCurrentStep(dataUrl) {
        const step = steps[currentStepIndex];
        if (step === "id_front") {
            frontImage = dataUrl;
            previewFront.src = dataUrl;
        } else if (step === "id_back") {
            backImage = dataUrl;
            previewBack.src = dataUrl;
        } else if (step === "selfie") {
            selfieImage = dataUrl;
            previewSelfie.src = dataUrl;
        }

        if (frontImage && backImage && selfieImage) {
            submitBtn.disabled = false;
        }
    }

    function resetCurrentStep() {
        captured = false;
        processing = false;
        stableFrames = 0;
        lastRect = null;
        hideDebugRect();
        manualBtn.disabled = false;
        restartBtn.disabled = true;
        nextStepBtn.disabled = true;
        statusEl.textContent = "Tekrar deniyorsun, kamerayı hedefe doğrult.";
        startProcessingLoop();
    }

    // Adım değişince kamerayı da yeniden init ediyoruz
    function goToNextStep() {
        if (currentStepIndex < steps.length - 1) {
            currentStepIndex++;
            captured = false;
            processing = false;
            stableFrames = 0;
            lastRect = null;
            hideDebugRect();
            updateUIForStep();
            initCamera();   // kamera burada ön/arka olarak yeniden seçiliyor
        }
    }

    async function submitAll() {
        if (!frontImage || !backImage || !selfieImage) return;

        const payload = {
            id_front: frontImage.split(",")[1],
            id_back: backImage.split(",")[1],
            selfie: selfieImage.split(",")[1],
            source: "browser_auto_capture_wizard"
        };

        console.log("Gönderilecek payload (kısaltılmış):", {
            ...payload,
            id_front: payload.id_front.slice(0, 30) + "...",
            id_back: payload.id_back.slice(0, 30) + "...",
            selfie: payload.selfie.slice(0, 30) + "..."
        });

        statusEl.textContent = "Simüle: payload konsola yazıldı (backend bağlanınca burası aktif edilecek).";
    }

    restartBtn.addEventListener("click", resetCurrentStep);
    manualBtn.addEventListener("click", manualCapture);
    nextStepBtn.addEventListener("click", goToNextStep);
    submitBtn.addEventListener("click", submitAll);

