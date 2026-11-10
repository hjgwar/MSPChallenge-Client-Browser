import Shepherd from 'https://cdn.jsdelivr.net/npm/shepherd.js/dist/esm/shepherd.mjs';

function disableNextButton(btn) {
    if (!btn) return;
    btn.disabled = true;
    btn.style.opacity = '0.2';
}

function enableNextButton(btn) {
    if (!btn) return;
    btn.disabled = false;
    btn.style.opacity = '1';
    btn.style.color = '#FFD700'; // Flash gold
    btn.animate(
        [{ transform: 'scale(1)' }, { transform: 'scale(1.4)' }, { transform: 'scale(1)' }],
        { duration: 400 }
    );
}

function addButtons(prevPartId, nextPartId) {
    const buttonsVar = [
        {
            action() {
                return this.back();
            },
            classes: 'shepherd-backbutton',
            text: '&lt;'
        },
        {
            action() {
                return this.next();
            },
            classes: 'shepherd-nextbutton',
            text: '&gt;'
        }
    ]
    if (prevPartId) {
        buttonsVar.push({
            action() {
                return this.show(prevPartId);
            },
            classes: 'shepherd-prevpartbutton',
            text: 'Back to previous part'
        });
    }
    if (nextPartId) {
        buttonsVar.push({
            action() {
                return this.show(nextPartId);
            },
            classes: 'shepherd-nextpartbutton',
            text: 'Skip to next part'
        });
    }
    return buttonsVar;
}

export function initiateTutorial() {
    const tour = new Shepherd.Tour({
        defaultStepOptions: {
            cancelIcon: {
                enabled: true
            },
            scrollTo: { behavior: 'smooth', block: 'center' }
        }
    });

    // 1. Keep sessionStorage updated as the user advances
    tour.on('show', (e) => {
        if (e.step) {
            sessionStorage.setItem('tour_current_step', e.step.id);
            sessionStorage.setItem('tour_active', 'true');
        }
    });

    // 2. Clear state if the user cancels or finishes the tour
    const clearTourSession = () => {
        sessionStorage.removeItem('tour_current_step');
        sessionStorage.removeItem('tour_active');
    };
    tour.on('cancel', clearTourSession);
    tour.on('complete', clearTourSession);

    // tour step definitions
    tour.addStep({
        id: 'welcome',
        title: 'Welcome',
        classes: 'shepherd-fullscreen',
        cancelIcon: {
            enabled: false
        },
        text: 'We recommend you follow this tutorial to get to know the basic features of MSP Challenge.',
        buttons: [
            {
                action() {
                    return this.cancel();
                },
                classes: 'btn btn-warning',
                text: 'Cancel'
            },
            {
                action() {
                    return this.next();
                },
                classes: 'btn btn-success',
                text: 'OK'
            }
        ]
    });

    tour.addStep({
        id: 'getstarted',
        title: 'Let\'s get started!',
        classes: 'shepherd-fullscreen',
        text: '<p>You are now a marine and maritime spatial planner! <br/>Your job is to create, implement and evaluate spatial plans for this area.</p><p>You will be working together with other players logged on to this session.</p>',
        buttons: addButtons('', ''),
    });

    tour.addStep({
        id: 'part1',
        title: 'Part 1<br/>Interface Basics',
        classes: 'shepherd-fullscreen',
        text: 'Before we start creating, implementing and evaluating spatial plans, let\'s walk through some basics.',
        buttons: addButtons('', 'part2'),
    });

    tour.addStep({
        id: 'interfacebasics1',
        title: 'Interface Basics [1/6]',
        classes: 'shepherd-bottomscreen',
        text: 'During this session you will govern the country associated with the colour you picked when logging in.',
        buttons: addButtons('', 'part2')    
    });

    tour.addStep({
        id: 'interfacebasics2',
        title: 'Interface Basics [2/6]',
        classes: 'shepherd-bottomscreen',
        text: 'Try zooming in and out with the scroll wheel of your mouse or by pinching on a trackpad or touchscreen.',
        buttons: addButtons('', 'part2'),
        when: {
            show() {
                const nextBtn = this.el.querySelector('.shepherd-nextbutton');
                const mapEl = document.getElementById('map');
                if (!nextBtn || !mapEl) return;

                disableNextButton(nextBtn);

                const onWheel = () => enableNextButton(nextBtn);
                const onTouch = (e) => { if (e.touches.length > 1) enable(); };

                mapEl.addEventListener('wheel', onWheel, { passive: true, once: true });
                mapEl.addEventListener('touchmove', onTouch, { passive: true });

                this._cleanup = () => {
                    mapEl.removeEventListener('wheel', onWheel);
                    mapEl.removeEventListener('touchmove', onTouch);
                };
            },
            hide() {
                this._cleanup?.();
            }
        }
    });

    tour.addStep({
        id: 'part2',
        title: 'Part 2<br/>Accessing Data & Exploring Consequences',
        classes: 'shepherd-fullscreen',
        text: 'Before you can start creating spatial plans, you first need to know what is already going on in this area of the ocean.',
        buttons: addButtons('part1', 'part3'),
        when: {
            show() {
                const layersBtn = document.getElementById('layers-button');
                const legendBtn = document.getElementById('legend-button');
                if (document.querySelector('.layer-panel')) {
                    layersBtn.click();
                }
                if (document.querySelector('.legend-panel')) {
                    legendBtn.click();
                }
            }
        }    
    });

    tour.addStep({
        id: 'accessingdata1',
        title: 'Accessing Data & Exploring Consequences [1/7]',
        classes: 'shepherd-bottomscreen',
        text: 'You can access different kinds of data by clicking on the layers button.',
        attachTo: {
            element: '#layers-button'
        },
        highlightClass: 'shepherd-target-highlight',
        buttons: addButtons('part1', 'part3'),
        when: {
            show() {
                const nextBtn = this.el.querySelector('.shepherd-nextbutton');
                const layersBtn = document.getElementById('layers-button');

                disableNextButton(nextBtn);

                const onLayersClick = () => {
                    enableNextButton(nextBtn);
                };
                layersBtn.addEventListener('click', onLayersClick, { once: true });

                this._cleanup = () => {
                    layersBtn.removeEventListener('click', onLayersClick);
                };
            },
            hide() {
                this._cleanup?.();
            }
        }
    });

    tour.addStep({
        id: 'accessingdata2',
        title: 'Accessing Data & Exploring Consequences [2/7]',
        classes: 'shepherd-bottomscreen',
        text: 'Under a category of your choice, pick a data layer to visualize on the map.',
        attachTo: {
            element: '.layer-panel'
        },
        highlightClass: 'shepherd-target-highlight',
        buttons: addButtons('part1', 'part3'),
        when: {
            show() {
                const nextBtn = this.el.querySelector('.shepherd-nextbutton');
                const layerPanel = document.querySelector('.layer-panel');
        
                disableNextButton(nextBtn);
        
                const onLayerChange = (e) => {
                    if (e.target && e.target.matches('.layer-panel-row input[type="checkbox"]')) {
                        enableNextButton(nextBtn);
                    }
                };
                layerPanel.addEventListener('change', onLayerChange);
        
                this._cleanup = () => {
                    layerPanel.removeEventListener('change', onLayerChange);
                };
            },
            hide() {
                this._cleanup?.();
            }
        }
    });

    tour.addStep({
        id: 'accessingdata3',
        title: 'Accessing Data & Exploring Consequences [3/7]',
        classes: 'shepherd-bottomscreen',
        text: 'You can visualize multiple data layers from different categories at the same time.<br/>If it becomes too crowded, you can uncheck a data layer to hide its content.',
        buttons: addButtons('part1', 'part3')    
    });

    tour.addStep({
        id: 'accessingdata4',
        title: 'Accessing Data & Exploring Consequences [4/7]',
        classes: 'shepherd-bottomscreen',
        text: 'All the selected data layers will appear as a list overview in the legend window. Click the button to open it.',
        attachTo: {
            element: '#legend-button'
        },
        highlightClass: 'shepherd-target-highlight',
        buttons: addButtons('part1', 'part3'),
        when: {
            show() {
                const nextBtn = this.el.querySelector('.shepherd-nextbutton');
                const legendBtn = document.getElementById('legend-button');

                disableNextButton(nextBtn);

                const onLegendClick = () => {
                    enableNextButton(nextBtn);
                };
                legendBtn.addEventListener('click', onLegendClick, { once: true });

                this._cleanup = () => {
                    legendBtn.removeEventListener('click', onLegendClick);
                };
            },
            hide() {
                this._cleanup?.();
            }
        }
    });

    tour.addStep({
        id: 'accessingdata5',
        title: 'Accessing Data & Exploring Consequences [5/7]',
        classes: 'shepherd-bottomscreen',
        text: 'The order in which data layers are shown in the legend window is the same order in which they are drawn on the map, on top of each other.<br/>You can change this order by dragging up or downand dropping a data layer.',
        buttons: addButtons('part1', 'part3')    
    });

    tour.addStep({
        id: 'accessingdata6',
        title: 'Accessing Data & Exploring Consequences [6/7]',
        classes: 'shepherd-bottomscreen',
        text: 'You can estimate possible consequences of your ideas by selecting the impact tool button.',
        attachTo: {
            element: '#dependencies-button'
        },
        highlightClass: 'shepherd-target-highlight',
        buttons: addButtons('part1', 'part3'),
        when: {
            show() {
                const nextBtn = this.el.querySelector('.shepherd-nextbutton');
                const dependenciesBtn = document.getElementById('dependencies-button');

                disableNextButton(nextBtn);

                const onDependenciesClick = () => {
                    sessionStorage.setItem('tour_current_step', 'accessingdata7');
                    enableNextButton(nextBtn);
                };
                dependenciesBtn.addEventListener('click', onDependenciesClick, { once: true });

                this._cleanup = () => {
                    dependenciesBtn.removeEventListener('click', onDependenciesClick);
                };
            },
            hide() {
                this._cleanup?.();
            }
        }
    });

    tour.addStep({
        id: 'accessingdata7',
        title: 'Accessing Data & Exploring Consequences [7/7]',
        classes: 'shepherd-bottomscreen',
        text: 'With this impact tool you can view the relationships between human activities and the marine ecosystem.<br/>Click on any node to view its relationships to other nodes.',
        buttons: addButtons('part1', 'part3'),
        when: {
            show() {
                const backBtn = this.el.querySelector('.shepherd-backbutton');
                const nextBtn = this.el.querySelector('.shepherd-nextbutton');
                const depGraphPanel = document.querySelector('.dep-graph');
        
                disableNextButton(nextBtn);
                
                const onDepGraphClick = (e) => {
                    if (e.target && e.target.closest('.node-lbl')) {
                        sessionStorage.setItem('tour_current_step', 'part3');
                        enableNextButton(nextBtn);
                    }
                };
                depGraphPanel.addEventListener('click', onDepGraphClick, true);
        
                this._cleanup = () => {
                    depGraphPanel.removeEventListener('click', onDepGraphClick, true);
                };
            },
            hide() {
                this._cleanup?.();
            }
        }
    });

    tour.addStep({
        id: 'part3',
        title: 'Part 3<br/>Creating & Implementing Plans',
        classes: 'shepherd-fullscreen',
        text: 'It is now up to you and your team to determine what goals you want to achieve with your spatial plans.',
        buttons: addButtons('part2', 'part4'),
        when: {
            show() {
                if (document.querySelector('.dep-back-btn')) {
                    sessionStorage.setItem('tour_current_step', 'part3');
                    document.querySelector('.dep-back-btn').click();
                }
            }
        }
    });

    // 3. Resume vs. Start logic
    const isTourActive = sessionStorage.getItem('tour_active') === 'true';
    const savedStepId = sessionStorage.getItem('tour_current_step');
    if (isTourActive && savedStepId && tour.getById(savedStepId)) {
        // Resume directly at the saved step for this page
        tour.show(savedStepId);
    } else if (window.location.pathname === '/game') {
        // Fresh start only on the game page
        tour.start();
    }
}