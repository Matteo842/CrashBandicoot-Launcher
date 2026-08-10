package io.github.matteo842.crashlauncher.ui

import android.content.Context
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.Space
import android.widget.TextView
import io.github.matteo842.crashlauncher.disc.DiscSelection
import kotlin.math.roundToInt

class LauncherScreen(context: Context) : FrameLayout(context) {
    var onSelectDisc: () -> Unit = {}
    var onStartGame: () -> Unit = {}
    var onSettings: () -> Unit = {}
    var onExit: () -> Unit = {}

    private val orange = Color.rgb(255, 138, 31)
    private val ink = Color.rgb(6, 16, 24)
    private val pale = Color.rgb(223, 235, 242)
    private val muted = Color.rgb(135, 158, 172)
    private val good = Color.rgb(80, 207, 139)
    private val bad = Color.rgb(255, 105, 97)

    private val bungee = loadTypeface("fonts/Bungee-Regular.ttf", Typeface.DEFAULT_BOLD)
    private val nunitoBold = loadTypeface("fonts/Nunito-ExtraBold.ttf", Typeface.DEFAULT_BOLD)
    private val nunito = loadTypeface("fonts/Nunito-SemiBold.ttf", Typeface.DEFAULT)

    private val selectButton = actionButton("SELECT DISC FOLDER", primary = true)
    private val startButton = actionButton("START GAME — RUNTIME PENDING", primary = false)
    private val statusDot = TextView(context)
    private val statusTitle = TextView(context)
    private val statusDetail = TextView(context)

    init {
        background = GradientDrawable(
            GradientDrawable.Orientation.TL_BR,
            intArrayOf(Color.rgb(4, 13, 20), Color.rgb(8, 31, 43), Color.rgb(4, 13, 20)),
        )
        setPadding(dp(24), dp(18), dp(24), dp(18))

        addView(buildTopBar())
        addView(buildContent())

        selectButton.setOnClickListener { onSelectDisc() }
        startButton.setOnClickListener { onStartGame() }
        startButton.isEnabled = false
        startButton.alpha = 0.56f
    }

    fun setBusy(busy: Boolean) {
        selectButton.isEnabled = !busy
        selectButton.alpha = if (busy) 0.62f else 1f
        if (busy) {
            startButton.isEnabled = false
            startButton.alpha = 0.56f
        }
        selectButton.text = if (busy) "SCANNING DISC FILES…" else "SELECT DISC FOLDER"
        if (busy) {
            statusDot.text = "●"
            statusDot.setTextColor(orange)
            statusTitle.text = "Inspecting selected folder"
            statusDetail.text = "Checking the cue sheet and its referenced disc image."
        }
    }

    fun showDisc(selection: DiscSelection) {
        statusDot.text = "●"
        statusDot.setTextColor(if (selection.isReady) good else bad)
        statusTitle.text = selection.title
        statusDetail.text = selection.detail
        startButton.isEnabled = selection.isReady
        startButton.alpha = if (selection.isReady) 1f else 0.56f
        startButton.text = if (selection.isReady) {
            "START GAME"
        } else {
            "START GAME — RUNTIME PENDING"
        }
    }

    private fun buildTopBar(): View = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        layoutParams = LayoutParams(LayoutParams.MATCH_PARENT, dp(34), Gravity.TOP)

        addView(label("UNOFFICIAL FAN PROJECT", 11f, orange, nunitoBold).apply {
            letterSpacing = 0.16f
        })
        addView(Space(context), LinearLayout.LayoutParams(0, 1, 1f))
        addView(label("ANDROID FOUNDATION  ·  0.1.0 DEV", 11f, muted, nunitoBold).apply {
            letterSpacing = 0.1f
        })
    }

    private fun buildContent(): View = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        layoutParams = LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.MATCH_PARENT).apply {
            topMargin = dp(34)
        }

        addView(buildHero(), LinearLayout.LayoutParams(0, LayoutParams.MATCH_PARENT, 0.92f))
        addView(Space(context), LinearLayout.LayoutParams(dp(24), 1))
        addView(buildDiscCard(), LinearLayout.LayoutParams(0, LayoutParams.MATCH_PARENT, 1.08f))
    }

    private fun buildHero(): View = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        gravity = Gravity.CENTER_VERTICAL
        setPadding(dp(12), dp(14), dp(8), dp(14))

        addView(label("CRASH", 29f, orange, bungee).apply { letterSpacing = 0.025f })
        addView(label("BANDICOOT", 35f, Color.WHITE, bungee).apply {
            letterSpacing = 0.015f
            setShadowLayer(14f, 0f, dp(3).toFloat(), Color.argb(130, 0, 0, 0))
        })
        addView(label("RECOMPILED", 17f, pale, nunitoBold).apply { letterSpacing = 0.24f })
        addView(label(
            "A native Android launcher foundation for the existing desktop project.",
            14f,
            muted,
            nunito,
        ), LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply {
            topMargin = dp(13)
            bottomMargin = dp(22)
        })

        addView(startButton, LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, dp(52)))
        addView(selectButton, LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, dp(52)).apply {
            topMargin = dp(10)
        })

        val secondary = LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            addView(actionButton("SETTINGS", false).apply {
                setOnClickListener { onSettings() }
            }, LinearLayout.LayoutParams(0, dp(44), 1f))
            addView(Space(context), LinearLayout.LayoutParams(dp(10), 1))
            addView(actionButton("EXIT", false).apply {
                setOnClickListener { onExit() }
            }, LinearLayout.LayoutParams(0, dp(44), 1f))
        }
        addView(secondary, LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply {
            topMargin = dp(10)
        })
    }

    private fun buildDiscCard(): View = LinearLayout(context).apply {
        orientation = LinearLayout.VERTICAL
        setPadding(dp(24), dp(22), dp(24), dp(20))
        background = rounded(Color.argb(238, 8, 24, 34), dp(18).toFloat(), Color.rgb(31, 61, 77))

        addView(label("ANDROID PORT", 12f, orange, nunitoBold).apply { letterSpacing = 0.18f })
        addView(label("Disc setup", 26f, Color.WHITE, nunitoBold),
            LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply {
                topMargin = dp(3)
            })
        addView(label(
            "Select the folder containing one .cue and its matching .bin. Android remembers the permission without requesting broad storage access.",
            14f,
            muted,
            nunito,
        ), LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply {
            topMargin = dp(8)
            bottomMargin = dp(22)
        })

        val status = LinearLayout(context).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.TOP
            setPadding(dp(16), dp(14), dp(16), dp(14))
            background = rounded(Color.rgb(5, 18, 27), dp(13).toFloat(), Color.rgb(26, 53, 68))

            statusDot.apply {
                text = "●"
                textSize = 15f
                setTextColor(muted)
                gravity = Gravity.TOP
            }
            addView(statusDot, LinearLayout.LayoutParams(dp(26), LayoutParams.WRAP_CONTENT))

            val copy = LinearLayout(context).apply {
                orientation = LinearLayout.VERTICAL
                statusTitle.apply {
                    text = "No disc folder selected"
                    textSize = 16f
                    setTextColor(pale)
                    typeface = nunitoBold
                }
                statusDetail.apply {
                    text = "The launcher accepts a legal dump supplied by the user; game data is never bundled."
                    textSize = 13f
                    setTextColor(muted)
                    typeface = nunito
                    setLineSpacing(0f, 1.12f)
                }
                addView(statusTitle)
                addView(statusDetail, LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT).apply {
                    topMargin = dp(4)
                })
            }
            addView(copy, LinearLayout.LayoutParams(0, LayoutParams.WRAP_CONTENT, 1f))
        }
        addView(status, LinearLayout.LayoutParams(LayoutParams.MATCH_PARENT, LayoutParams.WRAP_CONTENT))

        addView(Space(context), LinearLayout.LayoutParams(1, 0, 1f))
        addView(label(
            "FOUNDATION STATUS  ·  UI + STORAGE READY  ·  GAME RUNTIME NOT CONNECTED",
            10f,
            muted,
            nunitoBold,
        ).apply { letterSpacing = 0.08f })
    }

    private fun actionButton(text: String, primary: Boolean): Button = Button(context).apply {
        this.text = text
        isAllCaps = false
        textSize = if (primary) 14f else 12f
        typeface = nunitoBold
        letterSpacing = 0.07f
        setTextColor(if (primary) ink else pale)
        gravity = Gravity.CENTER
        stateListAnimator = null
        minHeight = 0
        minWidth = 0
        setPadding(dp(14), 0, dp(14), 0)
        background = if (primary) {
            rounded(orange, dp(12).toFloat(), Color.TRANSPARENT)
        } else {
            rounded(Color.rgb(13, 35, 47), dp(12).toFloat(), Color.rgb(35, 67, 83))
        }
    }

    private fun label(text: String, size: Float, color: Int, font: Typeface): TextView =
        TextView(context).apply {
            this.text = text
            textSize = size
            setTextColor(color)
            typeface = font
            includeFontPadding = false
            setLineSpacing(0f, 1.1f)
        }

    private fun rounded(fill: Int, radius: Float, stroke: Int): GradientDrawable =
        GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            setColor(fill)
            cornerRadius = radius
            if (stroke != Color.TRANSPARENT) setStroke(dp(1), stroke)
        }

    private fun loadTypeface(assetPath: String, fallback: Typeface): Typeface =
        runCatching { Typeface.createFromAsset(context.assets, assetPath) }.getOrDefault(fallback)

    private fun dp(value: Int): Int =
        (value * resources.displayMetrics.density).roundToInt()
}
